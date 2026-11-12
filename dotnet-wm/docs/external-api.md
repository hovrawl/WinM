# External commands API

HomeDesk exposes its running manager through a local Windows named pipe.
Third-party applications can use the CLI, the .NET client SDK, or implement
the version 1 pipe protocol directly. There is no HTTP endpoint or browser bridge.

The current API exposes workspace membership, logical workspace positions, and transferable monitor views.
`ManagerSnapshot.WorkspaceViews` contains monitor/workspace pairs; older workspace
`MonitorId` and `Active` values are compatibility projections. An unviewed workspace
uses `Guid.Empty` and `false`. Older hosts may omit `WorkspaceViews`, so clients
should treat that optional field as unavailable when it is null.

Each `desktop.windows` item includes `layoutOverflow`, which is true when the
window remains managed but its minimum size prevents it from being staged in the
current layout. The manager minimizes these windows; `minimized` separately
reports the observed native state, so clients can surface a failed minimize.
Use the existing `focus` command on an overflow window in its active workspace
to prioritize it for staging; when possible, the currently focused tile becomes
the replacement overflow window. The CLI `status` view also lists overflow titles.

The optional `desktop.configuredExclusions` array reports safely identifiable
visible candidates excluded by configured process/class names. Each entry has
`title`, `processName`, `className`, `monitorId`, `bounds`, `processNameExcluded`,
and `classNameExcluded`, but no actionable window ID. These entries never join
workspace membership or the tiling tree. Change configuration and restart the
host to include them; `setMode` does not override configured exclusions. Missing
or null means diagnostics are unavailable on that host; empty means no matching
candidates were observed. This is not a full native-window inventory. Changes
produce `window.updated` with the complete snapshot.

The separate [HomeDesk debug console](../src/HomeDesk.DebugUi/README.md)
and [HomeDeskManager](../src/HomeDeskManager/README.md) use this
SDK/IPC contract for their display and explicit controls. Neither creates desks
or owns manager state.

The Debug console can edit the host-selected configuration file through the SDK.
`HomeDeskClient.ReadConfigurationAsync()` returns the absolute path, raw file
content, SHA-256 revision, and validation status; `SaveConfigurationAsync()`
validates and atomically replaces that same file only when the revision matches.
The editor can explicitly merge the current snapshot's workspace names, layouts,
layered options, and positions while preserving other configuration fields and
monitor placement hints. Saves are disk-only and require a manager restart; live
view assignments, window membership, and native geometry are not persisted.

View activation, logical workspace positions, per-workspace `split`/`layered`
layout selection and layered width/spacing rules, and the spatial switcher are implemented. Workspace bounds,
columns/rows/Fibonacci rules, persistence of runtime desk state, and the full desk
overview are not. See [the project goal](project-goal.md) and
[the migration plan](desk-model-plan.md).

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
& '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'snapshot'
```

The response is a complete `ManagerSnapshot`:

- `instanceId`: identifies this manager lifetime; changes after a restart.
- `stateRevision`: identifies committed state; can remain unchanged for a no-op.
- `desktop.windows`: observable windows with `id`, `monitorId`, title, process/class
  names, pixel bounds, visibility, minimized/fullscreen flags, and mode. Includes
  default-ignored overlays so they can be targeted individually; `isPopup`,
  `isToolWindow`, `isOwned`, `isNoActivate`, `isIgnored`, and `tilingOverride`
  explain their eligibility (see below).
- `desktop.focusedWindowId`: the current focused managed window, or `null`.
- `desktop.monitors`: monitor `id`, device name, bounds, work area, and DPI.
- `workspaces`: workspace `id`, configured name, `layout` (`split` or `layered`),
  effective `layeredLayoutOptions` (`windowWidthRatio` and `horizontalOffset`),
  logical `position` (`x` and `y`), monitor ID, active flag, and assigned window IDs.
  Older hosts can omit position or layered options; spatial clients should treat
  missing position as unavailable and missing options as the documented defaults.
- `trees`: split trees keyed by **workspace ID**, not monitor ID. Each node has
  an ID, optional window ID, orientation, ratio, and first/second children.
- `paused`: whether automatic layout application is paused.
- `selection`: optional `{workspaceId, nodeId}` logical tiling-container selection;
  this does not change `desktop.focusedWindowId`.

Use these GUIDs as targets, not native window handles or process IDs. Numbered
shortcut slots are intentionally separate: monitor numbers follow configured
`monitorOrder` (or stable connected-device order), and workspace numbers follow
configuration order. Rediscover GUIDs after a manager restart and when targets disappear.
Workspace names are resolved case-insensitively by the manager, so configured
names are convenient for shortcuts and scripts.

## Complete manager command reference

Send the JSON objects below to `command -` or as the `payload` of a pipe
`command` request. JSON property names are case-sensitive camelCase. Use string
enum values, not integers. Omit unused fields; unknown and duplicate properties
are rejected by the server.

Window-targeted commands accept optional `windowId`. When omitted or `null`,
they use the focused managed window; if there is no such window they fail with
`not_found` (except `focusCycle`, which can start without focus). In the examples,
replace `<window-guid>`, `<workspace-guid>`, and
`<monitor-guid>` with GUIDs from a snapshot.

| `kind` | Required fields | Behavior | Example command JSON |
| --- | --- | --- | --- |
| `focus` | None; optional `direction` | Focus the target window, or the nearest eligible window in that direction within its workspace; if none exists, search the nearest directional monitor's viewed workspace. Focusing a `layoutOverflow` window in its active workspace prioritizes it for staging and can minimize the focused tile in its place. | `{"kind":"focus","direction":"right"}` |
| `focusMonitor` | `monitorNumber` from 1 through 10 | Focus the nearest eligible window on that numbered monitor without changing its workspace view. An empty or disconnected slot returns `not_found`. | `{"kind":"focusMonitor","monitorNumber":2}` |
| `focusCycle` | None; optional `cycleDirection`, `mode`, workspace target, `windowId` | Cycle eligible windows within an active workspace, wrapping at either end; for layered mode, this brings the selected page to the front. | `{"kind":"focusCycle","cycleDirection":"next","mode":"tiling"}` |
| `move` | `direction` | Swap the target tile with a directional tiling neighbor in its workspace; not a pixel-position move. | `{"kind":"move","direction":"left"}` |
| `moveSpatial` | `direction` | Float and place the target at the nearest directional local window's origin. If none exists in its workspace, move its membership to the neighboring monitor's viewed workspace and center it there. It never swaps another window. | `{"kind":"moveSpatial","direction":"right"}` |
| `resize` | `direction`, `amount` | Adjust the nearest ancestor split on the direction's axis. | `{"kind":"resize","direction":"right","amount":0.05}` |
| `setSplit` | `orientation` | Change the selected split when selection exists and `windowId` is omitted; otherwise change the target tile's parent split (or the next appended split for a single tile). | `{"kind":"setSplit","orientation":"vertical"}` |
| `selectContainer` | None; optional `nodeId`, or `windowId` and `navigation` | Select a node of an active workspace's tiling tree without changing native focus. | `{"kind":"selectContainer","navigation":"parent"}` |
| `clearSelection` | None | Clear logical container selection; repeated clearing is a no-op. | `{"kind":"clearSelection"}` |
| `setMode` | `mode` | Set the target to `floating` (excluded from tiling) or explicitly `tiling` (overrides semantic defaults). | `{"kind":"setMode","mode":"floating"}` |
| `toggleMode` | None | Switch the target between tiled and untiled; default-ignored windows are opted into tiling on the first toggle. | `{"kind":"toggleMode","windowId":"<window-guid>"}` |
| `pause` | `paused` | Set pause explicitly to `true` or `false`; not a toggle. | `{"kind":"pause","paused":true}` |
| `redraw` | None | Rescan and reconcile the desktop; applies layouts when not paused. | `{"kind":"redraw"}` |
| `exit` | None | Request manager shutdown and restoration of manager-owned native changes. | `{"kind":"exit"}` |
| `activateWorkspace` | Exactly one of `workspaceId`, `workspaceName`; optional `monitorId` | Show the workspace on the target monitor. If it is visible elsewhere, transfer its view; the destination's prior workspace remains unviewed. Without `monitorId`, use the focused window's connected monitor, then the primary monitor. | `{"kind":"activateWorkspace","workspaceName":"2","monitorId":"<monitor-guid>"}` |
| `moveToWorkspace` | Exactly one of `workspaceId`, `workspaceName`, or `workspaceNumber` | Assign the target window to an existing workspace; does not activate the destination. `workspaceNumber` is a one-based slot in configured workspace order, from 1 through 10. | `{"kind":"moveToWorkspace","windowId":"<window-guid>","workspaceNumber":2}` |
| `moveToMonitor` | `monitorId` | Assign the target window to the workspace currently shown by the destination monitor; this does not transfer a workspace view. | `{"kind":"moveToMonitor","monitorId":"<monitor-guid>"}` |
| `setWorkspaceLayout` | Exactly one of `workspaceId`, `workspaceName`; `layout`; optional `layeredLayoutOptions` | Set a workspace's `split` or `layered` layout and reflow its active view unless paused. Option updates can be sent while keeping the same layout; layout and options roll back together on native action failure. | `{"kind":"setWorkspaceLayout","workspaceName":"2","layout":"layered","layeredLayoutOptions":{"windowWidthRatio":0.8,"horizontalOffset":32}}` |
| `setWorkspacePosition` | Exactly one of `workspaceId`, `workspaceName`; `position` | Set logical desk coordinates without moving or resizing native windows; commits `workspace.updated`. | `{"kind":"setWorkspacePosition","workspaceName":"2","position":{"x":520,"y":-80}}` |

Argument details:

- `direction`: `left`, `right`, `up`, or `down`.
- `monitorNumber`: one-based monitor slot from 1 through 10. Slots in a configured
  `monitorOrder` are fixed even while those devices are disconnected; without a
  configured order, connected monitors are ordered with the primary first, then
  by device name and GUID. Unlisted connected devices follow configured slots.
- `workspaceNumber`: one-based configured workspace slot from 1 through 10; it is
  accepted only by `moveToWorkspace`. Workspace names/IDs remain preferred for
  clients that need an explicit stable identity.
- `orientation`: `horizontal` means side-by-side tiles; `vertical` means stacked
  tiles.
- `mode`: `tiling` or `floating`.
- `position`: logical `x` and `y` desk units. Both values must be finite and in
  `[-100000,100000]`; this does not represent monitor pixels or resize a workspace.
- `layout`: `split` or `layered`. `split` uses the saved split tree and gap settings.
  `layered` places individually fitting tiled windows in a full-height horizontal
  page stack. Only windows whose own minimum width or height exceeds the work area
  overflow.
- `layeredLayoutOptions`: optional object with `windowWidthRatio` (default `1.0`,
  finite in `(0,1]`) and `horizontalOffset` (default `32`, integer DIPs in `[0,256]`).
  The ratio is a preferred maximum page width relative to usable work width. At the
  default 100%, a single page fills the padded work area and each extra page reduces
  all page widths by one offset while keeping the stack's far edge aligned. The
  offset is a uniform start-to-start spacing and compresses when needed to preserve
  fitting windows' minimum widths. Effective values are returned in workspace snapshots.
- `amount`: a finite fraction in `(0, 0.8]`, not pixels or a percentage integer.
  `0.05` changes the split ratio by five percentage points. `left`/`up` decrease
  the first child's share; `right`/`down` increase it, regardless of which child
  contains the target. Ratios are clamped to `[0.1, 0.9]`; DPI-scaled gaps
  and window minimum sizes constrain placement. A layout that cannot stage every
  window at its minimum size marks the remaining windows `layoutOverflow` and
  minimizes them. A missing matching split or an adjustment already at the ratio
  limit returns `not_found`.
- `paused`: a JSON boolean. Pausing suspends automatic placement and workspace
  visibility application; it is not an API lock. Logical commands can still
  change state while paused, and unpausing reapplies it.
- Workspaces must already exist in configuration; there is no external command
  to create, delete, or rename them. See [configuration](configuration.md).
- Minimized, invisible, and floating windows are excluded from tiling. Fullscreen,
  popup, tool, owned, and no-activate windows are ignored by default, but can be
  explicitly opted into tiling. Not every observable window is a valid tiling
  movement target.

There is no command for arbitrary pixel bounds, launching applications, closing
an individual window, or changing configuration. `exit` stops **HomeDesk**, not
the managed applications.

### Filtered focus cycling

`focusCycle` defaults to `cycleDirection: "next"`; `"previous"` reverses the
cycle. Optional `mode: "tiling"` or `"floating"` filters candidates; omitting
`mode` includes both. Invisible, minimized, default-ignored, and zero-area
windows are excluded. Tiles follow split-tree leaf order, including manual
reordering; floating windows follow tiles in stable GUID order.
For layered workspaces, use `mode: "tiling"` to cycle pages; native focus raises
the selected window without changing the saved split tree or workspace membership.

The starting window is `windowId` or the observed focused window. The workspace
defaults to that window's assignment; without a managed focused window, it is
the primary monitor's active workspace (falling back to device-name/GUID order).
An optional `workspaceId` or case-insensitive `workspaceName` overrides the scope.
It must be active; this command never activates a workspace. An explicit window
must belong to that scope. With no matching starting window, next selects the
first match and previous the last. An empty filter returns `not_found`; cycling
the already-focused sole match succeeds without native actions or a new revision.

Successful observed focus changes use `focus.changed` and clear container
selection. Failed native focus returns its error and does not assume focus moved.
Use `cycleDirection`, not the spatial `direction` field.

Directional `focus` searches visible, non-minimized, non-overflow, non-ignored
windows by geometric distance in the current active workspace. If no local target
exists, it searches the nearest monitor in that direction and its viewed workspace;
it does not activate or transfer a workspace view. `moveSpatial` requires an active,
visible, non-minimized, non-overflow target and a resumed manager. Locally it sets
the target to floating and places it at the nearest directional window's top-left
origin, clamped to the work area. Without a local target it transfers the window's
membership to the neighboring monitor's viewed workspace and centers it there.
Window size is preserved where destination minimums and work-area bounds allow;
native failures restore the prior mode and workspace membership as far as the
observed platform state permits. Existing `move` remains the tiling-tree swap.
Default manager shortcuts include Alt+H/J/K/L for directional focus and
Alt+Shift+H/J/K/L for the matching spatial move (H=left, J=down, K=up, L=right).
They can be disabled or overridden through manager shortcut configuration.

```powershell
'{"kind":"focusCycle","cycleDirection":"previous","mode":"floating"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
```

```csharp
await client.ExecuteAsync(new ManagerCommand(CommandKind.FocusCycle,
    Mode: WindowMode.Tiling, CycleDirection: FocusCycleDirection.Next), cancellationToken);
```

The same command can be used in a configured shortcut binding.

### Container selection

`selectContainer` accepts a `nodeId` discovered in `trees`, or starts at the
`windowId` tile (the focused tile when omitted). `nodeId` cannot be combined
with `windowId` or `navigation`. Navigation values are `parent`, `firstChild`,
`secondChild`, `sibling`, and `root`. With navigation and no explicit window,
the existing selection is the starting node; without navigation, the focused
tile is selected. Missing nodes, inactive workspaces, untiled windows, and
navigation beyond the tree boundary return `not_found` without changing selection.

Selection persists across redraws while its node remains in an active workspace.
An observed focus change, workspace deactivation, or node removal clears it.
Each selection change emits `selection.changed` with a complete snapshot.
`setSplit` without `windowId` operates directly on a selected branch; a selected
leaf returns `not_found`. An explicit `windowId` retains the original parent-split
behavior. Other window commands do not target the selection.

CLI example: select the focused tile's parent, then orient that container:

```powershell
'{"kind":"selectContainer","navigation":"parent"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"setSplit","orientation":"vertical"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
```

The .NET SDK supports the same typed contracts through `ExecuteAsync`:

```csharp
await client.ExecuteAsync(new ManagerCommand(CommandKind.SelectContainer,
    Navigation: ContainerNavigation.Parent), cancellationToken);
await client.ExecuteAsync(new ManagerCommand(CommandKind.SetSplit,
    Orientation: SplitOrientation.Vertical), cancellationToken);
await client.ExecuteAsync(new ManagerCommand(CommandKind.ClearSelection), cancellationToken);
```

### Window defaults and manual overrides

Defaults use native window semantics, not a blanket application class blacklist.
For example, the Teams sharing control bar has `WS_POPUP` and is ignored while
an ordinary Teams window with the same `TeamsWebView` class can still tile.
`isPopup` reflects `WS_POPUP`; `isToolWindow` reflects `WS_EX_TOOLWINDOW`;
`isOwned` reflects a native owner; `isNoActivate` reflects `WS_EX_NOACTIVATE`.
Fullscreen means the visible bounds cover the monitor bounds, not merely its
work area; ordinary maximized windows are not automatically fullscreen.

Default-ignored windows report `mode: "floating"` and `isIgnored: true`. They
are observed and assigned an identity/workspace but are not tiled, clamped into
the work area, transferred by drag/drop, or hidden when switching workspaces.
This differs from ordinary floating windows, which still follow workspaces.
Child, shell, cloaked, application-hidden, and configured process/class exclusions
remain outside discovery and cannot be opted in by command.

`setMode` or `toggleMode` stores a per-window override. `tilingOverride` is
`null` for automatic defaults, `true` for explicitly tiled, and `false` for
explicitly untiled. Explicit tiling overrides fullscreen/popup/tool/owned/no-activate
defaults but never application-minimized or application-hidden state. Opting an
overlay back out leaves it untouched again. Overrides survive rescans and style
changes until the window disappears or the host restarts; they are not saved
per application or class. Classification and mode changes publish `window.updated`.

```csharp
await client.ExecuteAsync(new ManagerCommand(CommandKind.ToggleMode, WindowId: window.Id), token);
// Or choose a known state instead of toggling:
await client.ExecuteAsync(new ManagerCommand(CommandKind.SetMode, WindowId: window.Id, Mode: WindowMode.Floating), token);
```

### PowerShell examples

From the repository root after publishing the CLI:

```powershell
'{"kind":"focus","direction":"right"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"resize","direction":"left","amount":0.05}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"setMode","mode":"tiling"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"toggleMode","windowId":"<window-guid>"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"pause","paused":false}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"activateWorkspace","workspaceName":"2"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
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
| `status` | Optional `--reconnect`, `--pipe NAME` | Four human-readable lines updated in place; redirected output appends blocks |
| `install-startup` | `--host ABSOLUTE_EXE`; optional `--config ABSOLUTE_JSON` | Registration result JSON |
| `uninstall-startup` | None | Registration result JSON |
| `help`, `--help`, `-h` | None | Human-readable usage |

Startup verbs are local CLI operations, **not pipe commands**. They modify only
the per-user `HomeDesk` logon registry value and do not start the Host. Details
and path restrictions are in the [CLI README](../src/HomeDesk.Cli/README.md).

For an interactive overview, use `HomeDesk.Cli.exe status`. It subscribes to all
events and displays the focused window's workspace, focused managed window, and latest
event. Without managed focus it lists all active workspaces. Configuration direction
is marked unavailable because the host does not expose it. Use `watch`, not `status`,
for JSON integrations. See the [live status guide](../src/HomeDesk.Cli/README.md#live-status).

Example Python integration (standard library only):

```python
import json
import subprocess

CLI = r"C:\Apps\HomeDesk\cli\HomeDesk.Cli.exe"

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

Reference `src\HomeDesk.Client\HomeDesk.Client.csproj` from your application
(or distribute its built assembly and dependencies). The SDK targets `net10.0`
and references the `HomeDesk.Contracts` project and namespace;
pipe connection requires Windows. No
published NuGet package is assumed here.

```powershell
dotnet 'add' 'C:\Path\To\YourApp\YourApp.csproj' 'reference' '.\src\HomeDesk.Client\HomeDesk.Client.csproj'
```

The following can be used in a .NET 10 console application's `Program.cs`:

```csharp
using System.Text.Json;
using HomeDesk.Client;
using HomeDesk.Contracts;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

await using var client = await HomeDeskClient.ConnectAsync(
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

Envelope failures throw `HomeDeskRemoteException` (with `ErrorCode` and
`StateRevision`); also check returned `CommandResult.Success`. Transport and
connection failures require a fresh client. Dispose the subscription/client to
release resources. There is one active subscription and one event consumer per
connection; requests can run concurrently.

For a reconnecting read-only feed use `HomeDeskClient.WatchAsync(eventTypes:
[ManagerEventTypes.LayoutChanged], reconnect: true, cancellationToken: stop.Token)`. It reconnects
on transport errors/connect timeouts, not protocol, permission, remote, or
consumer-lag errors. See the [SDK README](../src/HomeDesk.Client/README.md) for
timeouts, cancellation, queue capacity, and reconnection details.

Use `ManagerEventTypes` from `HomeDesk.Contracts` instead of handwritten event
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
`HomeDesk.v1.<SIDHASH>.<SESSION>`, where `SIDHASH` is the first 24 **uppercase**
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
are `query.snapshot` (`{}`), `configuration.read` (`{}`),
`configuration.save` (`{"revision":"<sha256>","content":"<configuration-json>"}`),
`command` (a command object), `subscribe`
(`{"eventTypes":["layout.changed"]}` or `{}` for all), and `unsubscribe`
(`{"subscriptionId":"<subscription-guid>"}`). Subscribe returns a
`SubscriptionResult` with the subscription ID.

Responses have `kind: "response"`, correlated `requestId`, `success`,
`errorCode`, `message`, `stateRevision`, and operation-specific `payload`.
Demultiplex by `kind`: events can arrive before a response, including the initial
snapshot **before the subscribe response**. Unsubscribe can also be preceded by
queued events. The full wire specification is in [protocol.md](protocol.md).

## Events, consistency, and failure handling

Available event types (the .NET constants are in `HomeDesk.Contracts`):

| .NET constant | Wire/CLI name |
| --- | --- |
| `ManagerEventTypes.Snapshot` | `snapshot` |
| `ManagerEventTypes.WindowManaged` | `window.managed` |
| `ManagerEventTypes.WindowUnmanaged` | `window.unmanaged` |
| `ManagerEventTypes.WindowUpdated` | `window.updated` |
| `ManagerEventTypes.FocusChanged` | `focus.changed` |
| `ManagerEventTypes.SelectionChanged` | `selection.changed` |
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

Common errors include `invalid_argument`, `configuration_conflict`,
`configuration_io`, `not_found`, `not_ready`, `busy`,
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