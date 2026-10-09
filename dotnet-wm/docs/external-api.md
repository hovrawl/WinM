# External commands API

WinManager exposes its running manager through a local Windows named pipe.
Third-party applications can use the CLI, the .NET client SDK, or implement
the version 1 pipe protocol directly. There is no HTTP endpoint or browser bridge.

## Choose an integration

| Integration | Best for | Interface |
| --- | --- | --- |
| CLI subprocess | Scripts or applications in any language | JSON stdin/stdout; newline-delimited events |
| .NET SDK | C# applications, desktop tools, status bars | Typed queries, commands, and subscriptions |
| Direct named pipe | Non-.NET applications that need a persistent connection | Length-prefixed UTF-8 JSON envelopes |

The Host must already be running; connecting does not launch it. Clients must
run locally as the same Windows user in the same Windows session. Do not start
the Host on a desktop managed by another window manager. See the root
[README](../README.md) for publishing and starting the Host and CLI.

## Discover targets first

```powershell
& '.\publish\cli\WinManager.Cli.exe' 'snapshot'
```

The response is a complete `ManagerSnapshot`:

- `instanceId`: identifies this manager lifetime; changes after a restart.
- `stateRevision`: identifies committed state; can remain unchanged for a no-op.
- `desktop.windows`: managed windows with `id`, `monitorId`, title, process/class
  names, pixel bounds, visibility, minimized/fullscreen flags, and mode.
- `desktop.focusedWindowId`: the current focused managed window, or `null`.
- `desktop.monitors`: monitor `id`, device name, bounds, work area, and DPI.
- `workspaces`: workspace `id`, configured name, monitor ID, active flag, and
  assigned window IDs.
- `trees`: split trees keyed by **workspace ID**, not monitor ID. Each node has
  an ID, optional window ID, orientation, ratio, and first/second children.
- `paused`: whether automatic layout application is paused.

Use these GUIDs as targets, not native window handles, process IDs, or array
indexes. Rediscover IDs after a manager restart and when targets disappear.
Workspace names are resolved case-insensitively by the manager, so configured
names are convenient for shortcuts and scripts.

## Complete manager command reference

Send the JSON objects below to `command -` or as the `payload` of a pipe
`command` request. JSON property names are case-sensitive camelCase. Use string
enum values, not integers. Omit unused fields; unknown and duplicate properties
are rejected by the server.

Window-targeted commands accept optional `windowId`. When omitted or `null`,
they use the focused managed window; if there is no such window they fail with
`not_found`. In the examples, replace `<window-guid>`, `<workspace-guid>`, and
`<monitor-guid>` with GUIDs from a snapshot.

| `kind` | Required fields | Behavior | Example command JSON |
| --- | --- | --- | --- |
| `focus` | None; optional `direction` | Focus the target window, or its directional neighbor if a direction is supplied. | `{"kind":"focus","windowId":"<window-guid>"}` |
| `move` | `direction` | Swap the target tile with a directional tiling neighbor in its workspace; not a pixel-position move. | `{"kind":"move","direction":"left"}` |
| `resize` | `direction`, `amount` | Adjust the nearest ancestor split on the direction's axis. | `{"kind":"resize","direction":"right","amount":0.05}` |
| `setSplit` | `orientation` | Change the target tile's parent split orientation; for a single tile, set the next appended split's orientation. | `{"kind":"setSplit","orientation":"vertical"}` |
| `setMode` | `mode` | Set the target to `floating` (excluded from tiling) or `tiling` (included when eligible). | `{"kind":"setMode","mode":"floating"}` |
| `pause` | `paused` | Set pause explicitly to `true` or `false`; not a toggle. | `{"kind":"pause","paused":true}` |
| `redraw` | None | Rescan and reconcile the desktop; applies layouts when not paused. | `{"kind":"redraw"}` |
| `exit` | None | Request manager shutdown and restoration of manager-owned native changes. | `{"kind":"exit"}` |
| `activateWorkspace` | Exactly one of `workspaceId`, `workspaceName` | Activate an existing workspace on its assigned monitor, hiding eligible windows in the previous workspace. | `{"kind":"activateWorkspace","workspaceName":"2"}` |
| `moveToWorkspace` | Exactly one of `workspaceId`, `workspaceName` | Assign the target window to an existing workspace; does not activate the destination. | `{"kind":"moveToWorkspace","windowId":"<window-guid>","workspaceId":"<workspace-guid>"}` |
| `moveToMonitor` | `monitorId` | Assign the target window to the destination monitor's active workspace. | `{"kind":"moveToMonitor","monitorId":"<monitor-guid>"}` |

Argument details:

- `direction`: `left`, `right`, `up`, or `down`.
- `orientation`: `horizontal` means side-by-side tiles; `vertical` means stacked
  tiles.
- `mode`: `tiling` or `floating`.
- `amount`: a finite fraction in `(0, 0.8]`, not pixels or a percentage integer.
  `0.05` changes the split ratio by five percentage points. `left`/`up` decrease
  the first child's share; `right`/`down` increase it, regardless of which child
  contains the target. Ratios are clamped to `[0.1, 0.9]`; minimum window sizes
  and DPI-scaled gaps also constrain physical placement. A missing matching
  split or an adjustment already at the ratio limit returns `not_found`.
- `paused`: a JSON boolean. Pausing suspends automatic placement and workspace
  visibility application; it is not an API lock. Logical commands can still
  change state while paused, and unpausing reapplies it.
- Workspaces must already exist in configuration; there is no external command
  to create, delete, or rename them. See [configuration](configuration.md).
- Minimized, fullscreen, invisible, and floating windows are excluded from
  normal tiling. Not every managed window is a valid tiling movement target.

There is no command for arbitrary pixel bounds, launching applications, closing
an individual window, or changing configuration. `exit` stops **WinManager**, not
the managed applications.

### PowerShell examples

From the repository root after publishing the CLI:

```powershell
'{"kind":"focus","direction":"right"}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
'{"kind":"resize","direction":"left","amount":0.05}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
'{"kind":"setMode","mode":"tiling"}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
'{"kind":"pause","paused":false}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
'{"kind":"activateWorkspace","workspaceName":"2"}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
```

Prefer stdin over inline JSON arguments in PowerShell: Windows PowerShell can
strip embedded quotes from native arguments. Each invocation creates a new
connection. Add `--pipe NAME` if the Host was started with a custom pipe name.

## Integrating through the CLI (any language)

| CLI verb | Arguments | Output |
| --- | --- | --- |
| `snapshot` | Optional `--pipe NAME` | One `ManagerSnapshot` JSON object |
| `command` | JSON argument or `-` for stdin; optional `--pipe NAME` | One `CommandResult` JSON object on success |
| `watch` | Optional `--event-types TYPE,TYPE`, `--reconnect`, `--pipe NAME` | One `EventEnvelope` JSON object per line |
| `install-startup` | `--host ABSOLUTE_EXE`; optional `--config ABSOLUTE_JSON` | Registration result JSON |
| `uninstall-startup` | None | Registration result JSON |
| `help`, `--help`, `-h` | None | Human-readable usage |

Startup verbs are local CLI operations, **not pipe commands**. They modify only
the per-user `WinManager` logon registry value and do not start the Host. Details
and path restrictions are in the [CLI README](../src/WinManager.Cli/README.md).

Example Python integration (standard library only):

```python
import json
import subprocess

CLI = r"C:\Apps\WinManager\cli\WinManager.Cli.exe"

def call_cli(*args, payload=None):
    completed = subprocess.run(
        [CLI, *args],
        input=None if payload is None else json.dumps(payload),
        capture_output=True, text=True, encoding="utf-8", timeout=15,
        shell=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(completed.stderr.strip() or completed.stdout.strip())
    return json.loads(completed.stdout)

snapshot = call_cli("snapshot")
window_id = snapshot["desktop"]["focusedWindowId"]
if window_id is not None:
    result = call_cli("command", "-", payload={
        "kind": "setMode", "windowId": window_id, "mode": "floating"
    })
    if not result["success"]:
        raise RuntimeError(result["message"])
```

Use a deployed absolute path, pass arguments without shell execution, close
stdin after writing the command, and drain both stdout and stderr. For `watch`,
keep the subprocess alive and parse stdout line by line rather than waiting for
the process to finish. Stop it when the application closes.

CLI exit codes: `0` success/help; `2` usage/JSON error; `3` server rejection or
unsuccessful command; `4` connection/permissions/protocol/consumer-lag/startup
failure; `130` Ctrl+C cancellation. Errors are JSON on stderr; an unsuccessful
typed command result may instead be on stdout. Connections time out after five
seconds by default. A subprocess timeout does not prove a command never ran.

## Integrating through the .NET SDK

Reference `src\WinManager.Client\WinManager.Client.csproj` from your application
(or distribute its built assembly and dependencies). The SDK targets `net10.0`
and references `WinManager.Contracts`; pipe connection requires Windows. No
published NuGet package is assumed here.

```powershell
dotnet 'add' 'C:\Path\To\YourApp\YourApp.csproj' 'reference' '.\src\WinManager.Client\WinManager.Client.csproj'
```

The following can be used in a .NET 10 console application's `Program.cs`:

```csharp
using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

await using var client = await WinManagerClient.ConnectAsync(
    cancellationToken: stop.Token);
var snapshot = await client.QuerySnapshotAsync(stop.Token);

if (snapshot.Desktop.FocusedWindowId is { } windowId)
{
    var result = await client.ExecuteAsync(new ManagerCommand(
        CommandKind.SetMode, WindowId: windowId, Mode: WindowMode.Floating), stop.Token);
    if (!result.Success)
        throw new InvalidOperationException($"{result.ErrorCode}: {result.Message}");
}

await using var subscription = await client.SubscribeAsync(
    [ManagerEventTypes.WindowUpdated, ManagerEventTypes.LayoutChanged, ManagerEventTypes.PauseChanged], stop.Token);
try
{
    await foreach (var envelope in subscription.ReadEventsAsync(stop.Token))
    {
        var state = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;
        Console.WriteLine($"{envelope.EventType}: revision {state.StateRevision}");
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
```

Envelope failures throw `WinManagerRemoteException` (with `ErrorCode` and
`StateRevision`); also check returned `CommandResult.Success`. Transport and
connection failures require a fresh client. Dispose the subscription/client to
release resources. There is one active subscription and one event consumer per
connection; requests can run concurrently.

For a reconnecting read-only feed use `WinManagerClient.WatchAsync(eventTypes:
[ManagerEventTypes.LayoutChanged], reconnect: true, cancellationToken: stop.Token)`. It reconnects
on transport errors/connect timeouts, not protocol, permission, remote, or
consumer-lag errors. See the [SDK README](../src/WinManager.Client/README.md) for
timeouts, cancellation, queue capacity, and reconnection details.

Use `ManagerEventTypes` from `WinManager.Contracts` instead of handwritten event
names. Its public `const string` members work with `SubscribeAsync`, `WatchAsync`,
and `SubscribeRequest`, and can be used to compare `EventEnvelope.EventType` or in
`switch` case labels:

```csharp
switch (envelope.EventType)
{
    case ManagerEventTypes.Snapshot:
    case ManagerEventTypes.LayoutChanged:
        var state = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;
        Console.WriteLine($"Layout at revision {state.StateRevision}");
        break;
}
```

These are named string constants, not an enum; existing string filters and wire
JSON remain compatible. Omit the filter or pass `null` to receive all events;
an empty filter (`[]`) receives only the mandatory initial snapshot. Unknown
names are rejected with `invalid_argument`. The complete constant mapping is below.

## Direct pipe protocol (non-.NET persistent clients)

Use a local duplex byte-mode named pipe at `\\.\pipe\NAME`. The default `NAME` is
`WinManager.v1.<SIDHASH>.<SESSION>`, where `SIDHASH` is the first 24 **uppercase**
hex characters of SHA-256 over the UTF-8 current user SID string, and `SESSION`
is the client's Windows session ID. .NET callers can use
`Protocol.DefaultPipeName()`. A configured `--pipe` override replaces the name,
not the same-user/session restrictions.

When opening the pipe, permit identity inspection (the SDK uses
`TokenImpersonationLevel.Identification`). On native `CreateFile`, the equivalent
is `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`. Remote, cross-user,
cross-session, and unverifiable clients are disconnected.

Each frame consists of a four-byte little-endian signed length and that many
UTF-8 JSON bytes. Length must be `1..1048576`. Read/write all bytes, allowing
partial reads; raw JSON lines are **not** pipe frames. JSON nesting is limited
to 64. Here is a command envelope before framing:

```json
{
  "protocolVersion": 1,
  "kind": "request",
  "requestId": "07a092be-e55e-41e4-9187-2951b48ecded",
  "operation": "command",
  "payload": { "kind": "pause", "paused": true }
}
```

Generate a fresh nonempty request GUID for each request. The available operations
are `query.snapshot` (`{}`), `command` (a command object), `subscribe`
(`{"eventTypes":["layout.changed"]}` or `{}` for all), and `unsubscribe`
(`{"subscriptionId":"<subscription-guid>"}`). Subscribe returns a
`SubscriptionResult` with the subscription ID.

Responses have `kind: "response"`, correlated `requestId`, `success`,
`errorCode`, `message`, `stateRevision`, and operation-specific `payload`.
Demultiplex by `kind`: events can arrive before a response, including the initial
snapshot **before the subscribe response**. Unsubscribe can also be preceded by
queued events. The full wire specification is in [protocol.md](protocol.md).

## Events, consistency, and failure handling

Available event types (the .NET constants are in `WinManager.Contracts`):

| .NET constant | Wire/CLI name |
| --- | --- |
| `ManagerEventTypes.Snapshot` | `snapshot` |
| `ManagerEventTypes.WindowManaged` | `window.managed` |
| `ManagerEventTypes.WindowUnmanaged` | `window.unmanaged` |
| `ManagerEventTypes.WindowUpdated` | `window.updated` |
| `ManagerEventTypes.FocusChanged` | `focus.changed` |
| `ManagerEventTypes.MonitorAdded` | `monitor.added` |
| `ManagerEventTypes.MonitorRemoved` | `monitor.removed` |
| `ManagerEventTypes.MonitorUpdated` | `monitor.updated` |
| `ManagerEventTypes.WorkspaceActivated` | `workspace.activated` |
| `ManagerEventTypes.WorkspaceDeactivated` | `workspace.deactivated` |
| `ManagerEventTypes.WorkspaceUpdated` | `workspace.updated` |
| `ManagerEventTypes.LayoutChanged` | `layout.changed` |
| `ManagerEventTypes.PauseChanged` | `pause.changed` |
| `ManagerEventTypes.ApplicationExiting` | `application.exiting` |

Every event payload is a **complete snapshot**, not a delta. Subscriptions always
begin with an atomic `snapshot` event even if the filter excludes it. Replace or
reconcile your local state from that snapshot. Within one instance, sequences
increase but can have gaps; multiple event types may share a state revision.
After reconnect/restart obtain a fresh snapshot; there is no replay or resume
cursor. `application.exiting` carries final state after shutdown restoration,
but delivery to a slow or disconnected client is not guaranteed. The `exit`
command response acknowledges the request, not completion of restoration.

Drain event feeds promptly: bounded server/SDK queues disconnect lagging
consumers rather than silently dropping events. Dispatch UI updates separately
from reading the stream.

Common errors include `invalid_argument`, `not_found`, `not_ready`, `busy`,
`exiting`, `unsupported_version`, `unknown_operation`, `already_subscribed`, and
native-action errors. On missing targets, refresh the snapshot before choosing
another target. Commands commit logical intent; native actions are best-effort,
and snapshots report observed geometry. An unsuccessful native operation does
not universally imply logical state was rolled back. Query or subscribe to
confirm the actual outcome, especially for elevated applications.

Never blindly replay a command after cancellation, timeout, or disconnect: it
may already have executed. Relative commands such as `move` and `resize` can
produce a second change if retried. The SDK conservatively closes a connection
when an admitted request is canceled and never automatically retries commands.