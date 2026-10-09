### Local IPC, version 1

The Windows host exposes a duplex byte-mode named pipe. The default name is
`WinManager.v1.<first 24 hex digits of SHA-256(user SID)>.<session ID>` as returned
by `Protocol.DefaultPipeName()`. Use local machine `.`. Connections require
an explicit protected current-user SID-only DACL on native `CreateNamedPipe`,
with kernel-enforced `PIPE_REJECT_REMOTE_CLIENTS`, rather than the managed
`PipeOptions.CurrentUserOnly` flag. The server also verifies the client process
ID and Windows session and requires the same impersonated user SID.
Clients must allow impersonation for
the identity check; it is used only to inspect the caller, not execute commands.
Remote, other-user, cross-session, and unverifiable clients are disconnected.
Native locality/session validation lives in the Windows adapter, not Host.

### Framing and envelopes

Each message is a four-byte little-endian signed length followed by exactly
that many UTF-8 JSON bytes. Length must be in `[1, 1048576]`; partial reads are
supported. EOF within a frame, malformed JSON, invalid lengths, or excessive
JSON nesting disconnect that client. JSON depth is limited to 64. Property names
are case-sensitive camelCase; enum values are camelCase strings, not integers.
Unknown or duplicate request/argument properties are rejected.

Requests contain `protocolVersion: 1`, `kind: "request"`, a nonempty GUID
`requestId`, `operation`, and `payload`. Responses contain `protocolVersion`,
`kind: "response"`, the correlated `requestId`, `success`, `errorCode`, `message`,
`stateRevision`, and optional `payload`. A malformed request without a usable
ID receives `Guid.Empty` as its response ID. Errors do not mutate manager state.

### Operations

| Operation | Payload | Successful response payload |
| --- | --- | --- |
| `query.snapshot` | `{}` or `null` | Complete committed `ManagerSnapshot` |
| `command` | Typed `ManagerCommand` object; `kind` is required | `CommandResult`; also reflected in response fields |
| `subscribe` | `{"eventTypes": null}` or `{}` for all events; otherwise an array of known event names | `SubscriptionResult`, including `subscriptionId` |
| `unsubscribe` | `{"subscriptionId": "<GUID>"}` | None |

Commands support `focus`, `move`, `resize`, `setSplit`, `setMode`, `pause`,
`redraw`, `exit`, `activateWorkspace`, `moveToWorkspace`, `moveToMonitor`, and `toggleMode`.
Optional command fields are `windowId`, `direction`, `amount`, `orientation`,
`mode`, `paused`, `workspaceId`, `workspaceName`, and `monitorId`. Workspace
commands accept exactly one workspace GUID or case-insensitive configured name;
named targets are resolved atomically in the manager. Command-specific argument and
target validation is performed by the manager actor. For example, `pause`
requires `paused`, and `resize` requires `direction` and a fraction in `(0, 0.8]`.
`toggleMode` needs no arguments; an optional `windowId` selects the target,
otherwise it uses the focused observable window. `setMode: tiling` and toggling
into tiling override fullscreen/popup/tool/owned/no-activate defaults for that
window's lifetime. Snapshot windows include `isPopup`, `isToolWindow`, `isOwned`,
`isNoActivate`, `isIgnored`, and nullable `tilingOverride`; default-ignored
windows remain discoverable but are neither placed nor workspace-hidden.

One subscription is allowed per connection. An unsubscribe ID must belong to
that connection. Already queued events can precede an unsubscribe response;
after successful acknowledgement that subscription generates no more events.
A client may subscribe again after unsubscribing.

Common error codes include `unsupported_version`, `unknown_operation`,
`invalid_argument`, `not_ready`, `already_subscribed`, `not_found`, `busy`,
`exiting`, and native action errors passed through by the manager. Slow-output
clients are disconnected rather than sent an error into an already full queue.

### Events and consistency

An event contains `protocolVersion: 1`, `kind: "event"`, `instanceId`, `sequence`,
`stateRevision`, `eventType`, and `payload`. **Every event payload is a complete
committed `ManagerSnapshot`**, not a delta. Native handles are not identifiers;
clients use the GUIDs in the snapshot.

Event names:

- `snapshot`
- `window.managed`, `window.unmanaged`, `window.updated`
- `focus.changed`
- `monitor.added`, `monitor.removed`, `monitor.updated`
- `workspace.activated`, `workspace.deactivated`, `workspace.updated`
- `layout.changed`, `pause.changed`, `application.exiting`

.NET callers can use the public `ManagerEventTypes` string constants from
`WinManager.Contracts` in SDK filters and event comparisons instead of handwritten
names. See the [constant mapping and SDK examples](external-api.md).
The version 1 wire format remains string-based and unchanged.

Subscription registration runs atomically on the manager actor: an initial
`snapshot` event is enqueued before any newer events for that subscription,
regardless of its filter (even an empty filter). The correlated subscribe
response follows the initial snapshot and may be interleaved with newer events.
Clients must demultiplex by `kind` and correlate responses by `requestId`; they
must not assume that the next frame after a request is its response.

Sequence numbers strictly increase globally within an instance. Different
subscribers receive the same sequence for the same broadcast, but filtered
streams and subscription snapshots can have gaps. `stateRevision` identifies
committed state, increases when that state changes, and may repeat across
different event types describing the same commit. `application.exiting` carries
the final committed snapshot after shutdown restoration.

There is no replay, acknowledgement, or resume cursor. Reconnect and subscribe
to obtain a fresh atomic snapshot. A restarted manager has a new `instanceId`;
do not compare sequence numbers or revisions across instances.

### Bounds and shutdown

The server defaults to 16 clients and a 128-message output queue per client.
Client admission is bounded; additional connections wait until a slot is free.
Each connection processes at most one request at a time, so outstanding actor
requests are bounded by the client limit; manager admission can also return
`busy`. A single writer sends both events and responses from the same queue.
Publishing never waits on the actor. Queue overflow disconnects the lagging
client without silently dropping events, and disconnect cleanup unsubscribes
through the actor (retrying transient busy admission). A stalled write has a
10-second deadline. Server shutdown allows up to two seconds to drain queued
output before aborting it; final delivery cannot be guaranteed to slow or dead
clients. Stop the manager first if final exit-event delivery is desired.