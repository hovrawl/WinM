# WinManager pipe SDK

`WinManager.Client` targets the inherited `net10.0` and references **only** `WinManager.Contracts`.
Connection is Windows-only: local server `.`; `PipeOptions.CurrentUserOnly` verifies same-user ownership;
`TokenImpersonationLevel.Identification` prevents the server impersonating the client to perform actions.
The default name is `Protocol.DefaultPipeName()` (current user/session); explicit names remain local and same-user.
This is not authentication against another malicious process running as the same user.

```csharp
using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;

await using var client = await WinManagerClient.ConnectAsync(cancellationToken: token);
ManagerSnapshot snapshot = await client.QuerySnapshotAsync(token);
CommandResult result = await client.ExecuteAsync(new ManagerCommand(CommandKind.Pause, Paused: true), token);
await using var subscription = await client.SubscribeAsync([ManagerEventTypes.PauseChanged], token);
await foreach (EventEnvelope envelope in subscription.ReadEventsAsync(token))
{
    // Every payload is a full ManagerSnapshot; the first event is always "snapshot".
    var state = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions);
}
```

Use the public `ManagerEventTypes` string constants in `WinManager.Contracts` for
subscription filters and event comparisons (including `switch` case labels), e.g.
`envelope.EventType == ManagerEventTypes.PauseChanged`. They also work with
`WatchAsync` and `SubscribeRequest`. All 14 constants and their wire names are listed
in the [external API event reference](../../docs/external-api.md#events-consistency-and-failure-handling).
These are named constants, not an enum: filters and `EventEnvelope.EventType` remain
strings, so existing callers and version 1 JSON are unchanged. Omit the filter or
pass `null` for all events; `[]` receives only the mandatory initial snapshot.
Unknown event names are rejected by the server with `invalid_argument`.

Requests can run concurrently; frames are written serially and responses correlate by request ID.
Operations are `query.snapshot` (`{}`), `command` (`ManagerCommand`), `subscribe` (`SubscribeRequest`),
and `unsubscribe` (`{ subscriptionId: Guid }`). Subscribe consumes a `SubscriptionResult` response.
Envelope failures throw `WinManagerRemoteException` with error code and revision; a successful command
envelope returns the typed `CommandResult`, whose `Success` must also be checked.

One active subscription and one event consumer are allowed per connection because events carry no
subscription ID. The queue is installed **before** subscribe is sent, so a snapshot arriving before
the response is retained. `SubscribeAsync` waits for both the response and initial snapshot; newer
events are queued behind that snapshot in wire order, including when filters exclude `snapshot`.
Explicit `UnsubscribeAsync` or subscription disposal removes the server subscription and completes
the event stream; the same connection can then subscribe again. Client disposal closes the pipe,
terminating server-side resources and all pending requests without waiting for an unsubscribe reply.

The SDK event queue defaults to 256 envelopes (`ConnectAsync(eventQueueCapacity: ...)`). The protocol
reader uses nonblocking `TryWrite`: overflow **closes the entire connection**, fails pending requests,
and terminates the event stream with `WinManagerConsumerLagException` after buffered events drain.
No drop mode is used. Bad frames, versions, kinds, payloads, instance changes, or event ordering are
fatal `WinManagerProtocolException`s. Sequence gaps are permitted because event filters can skip types.

Connect defaults to a five-second timeout; `Timeout.InfiniteTimeSpan` is supported. All operations
accept cancellation tokens. Canceling a request after registration conservatively closes the
connection: a command may already have executed, so it is **never automatically replayed**.
Use a timeout token for subscribe if a server might fail to send its initial snapshot.

## Reconnection

Clients are reusable for concurrent requests and subscribe/unsubscribe cycles, not after disconnect.
Use a fresh `ConnectAsync`, or use the static watch generator:

```csharp
await foreach (var envelope in WinManagerClient.WatchAsync(
    eventTypes: [ManagerEventTypes.PauseChanged], reconnect: true, cancellationToken: token))
{
    // After every reconnection the first event is a fresh snapshot with the server's InstanceId.
    // Reconcile from this full snapshot; do not assume events during a disconnection were replayed.
}
```

`WatchAsync` reconnects only for transport `IOException` and connect `TimeoutException`, with a
one-second default retry delay. Each connection starts a fresh subscription and snapshot; sequence
and revision continuity are checked only within that subscription, and a restarted server may use
a different instance ID. Protocol errors, remote errors, permissions errors, and consumer lag are
fatal even with reconnect enabled. Cancellation/break/disposal closes the active connection.
This is resynchronization, **not durable event replay**; missed transitions during downtime cannot
be recovered. No commands are retried.

## Local validation

The package-free `Validation` executable uses actual same-user named pipes, not the engine or Host.
It is excluded from the production Client compile and is not registered in the parent solution.

```powershell
dotnet 'run' '--project' '.\src\WinManager.Client\Validation\WinManager.Client.Validation.csproj'
```

Run from `C:\Dev\NahFarn\WinM\dotnet-wm`. Parent-owned server integration remains separate.