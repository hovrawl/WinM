### Local IPC, version 1

This section documents the current version 1 protocol, including workspace-view
transfer, logical workspace positions, and per-workspace split/layered layout contracts.
Workspace bounds, additional layout rules, persistent desk state, and the full desk
overview remain planned; separate debug-console and spatial-switcher apps are available.
See [the project goal](project-goal.md) and
[the migration plan](desk-model-plan.md).

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
| `query.previewTargets` | `{"windowIds":["<GUID>", ...]}` | `PreviewTargetsResult` with short-lived native target identities or per-window failures |
| `configuration.read` | `{}` | `ConfigurationDocument` with the host-selected file path, raw UTF-8 content, SHA-256 revision, and validation status |
| `configuration.save` | `{"revision":"<sha256>","content":"<configuration-json>"}` | Updated `ConfigurationDocument`; invalid or stale writes return `invalid_argument` or `configuration_conflict` |
| `command` | Typed `ManagerCommand` object; `kind` is required | `CommandResult`; also reflected in response fields |
| `subscribe` | `{"eventTypes": null}` or `{}` for all events; otherwise an array of known event names | `SubscriptionResult`, including `subscriptionId` |
| `unsubscribe` | `{"subscriptionId": "<GUID>"}` | None |

`query.previewTargets` accepts 1–64 unique, nonempty managed window IDs from the
current snapshot. It never accepts a caller-supplied HWND. The response includes
`stateRevision`, `targets`, and `failures`; each target has `windowId`, signed
64-bit `handleValue`, `processId`, and 64-bit `processCreationTime`. A target
identity is transient and must be validated immediately before native preview
registration. Each requested ID appears exactly once in either list; inaccessible,
stale, minimized, or otherwise unavailable windows use a per-window failure code
and do not fail the whole query. Hosts without this operation return
`unknown_operation`; clients should keep a placeholder rather than retrying with
raw handles. The existing same-user/session pipe protections still apply.

Configuration operations are restricted to the path selected by the host's
`--config` option or its default `%LOCALAPPDATA%\WinManager\config.json`;
clients cannot provide paths. The host validates the complete replacement and
atomically replaces the file only when the submitted revision still matches.
External edits can race the final replace because ordinary editors do not share
the host's lock. IPC configuration content is limited to 128 KiB so its escaped
JSON remains within the frame limit. Saving does not update running host services;
restart the manager to apply the file.

Commands support `focus`, `focusMonitor`, `moveSpatial`, `move`, `resize`, `setSplit`, `setMode`, `setWorkspaceLayout`, `setWorkspacePosition`, `pause`,
`redraw`, `exit`, `activateWorkspace`, `moveToWorkspace`, `moveToMonitor`, `toggleMode`,
`selectContainer`, `clearSelection`, and `focusCycle`.
Optional command fields are `windowId`, `direction`, `amount`, `orientation`,
`mode`, `paused`, `workspaceId`, `workspaceName`, `monitorId`, `nodeId`, `navigation`,
`cycleDirection`, `layout`, `position`, `layeredLayoutOptions`, `monitorNumber`, and
`workspaceNumber`. Workspace commands accept exactly one workspace GUID or
case-insensitive configured name, except `moveToWorkspace` also accepts a one-based
`workspaceNumber` slot from 1 through 10; named targets are resolved atomically in
the manager. `focusMonitor` accepts `monitorNumber` from 1 through 10 and focuses an
eligible window on the corresponding configured/stable monitor slot without changing
its workspace view. `moveSpatial` accepts a direction and optional window ID. It
floats and positions the window beside a local directional neighbor, or transfers
membership to the neighboring monitor's viewed workspace and centers it when no
local target exists. Directional `focus` uses geometric nearest-window selection
within the active workspace, then looks to the nearest monitor in that direction.
Command-specific argument and target validation is performed by the manager actor. For example, `pause`
requires `paused`, and `resize` requires `direction` and a fraction in `(0, 0.8]`.
`activateWorkspace` uses optional `monitorId` to name the target monitor. Without
it, the manager selects the focused window's observed connected monitor, then the
primary monitor in stable device-name/GUID order. If no monitor is connected, it
returns `not_ready`. Selecting a workspace visible on another monitor transfers
its exclusive view; the destination's prior workspace remains unviewed. If a
source monitor needs a fallback, the manager uses another pre-existing unviewed
workspace or creates an empty one, without using the displaced destination view.
`setWorkspaceLayout` accepts `layout: split|layered`, optional
`layeredLayoutOptions`, and exactly one workspace ID or configured name. The options
contain `windowWidthRatio` (default `1.0`, finite and in `(0,1]`) and
`horizontalOffset` (default `32`, an integer from `0` to `256` DIPs). The ratio is a
preferred maximum page width; at the default 100%, a single page fills the padded
work area and each additional page reduces every page width by one offset, keeping
the stack's far edge aligned. Spacing compresses uniformly only as needed to respect
all fitting windows' minimum widths. Layered pages retain full work-area height. The
effective options and selected layout are included in each `WorkspaceInfo`.
With generated shortcuts enabled, Alt+H/J/K/L performs directional focus
(H=left, J=down, K=up, L=right), and Alt+Shift with those keys performs the
matching spatial move. The host can disable generated shortcuts or replace a
default binding through its configuration.

The command reflows the active view unless paused; on a native action failure the
manager reports the error and restores the previous layout and options. Workspace
configuration seeds restart defaults; runtime changes are not persisted to configuration.
`setWorkspacePosition` accepts a finite logical `{x,y}` position in the inclusive
range `[-100000,100000]` and exactly one workspace ID or configured name. It
updates desk state and publishes `workspace.updated` without native window actions.
`WorkspaceDefinition.position` seeds coordinates across restarts; omitted values
receive deterministic workspace-order defaults. `WorkspaceInfo.position` may be
absent on older hosts. Positions are independent of monitor pixels, DPI and zoom.
`toggleMode` needs no arguments; an optional `windowId` selects the target,
otherwise it uses the focused observable window. `setMode: tiling` and toggling
into tiling override fullscreen/popup/tool/owned/no-activate defaults for that
window's lifetime. Snapshot windows include `isPopup`, `isToolWindow`, `isOwned`,
`isNoActivate`, `isIgnored`, nullable `tilingOverride`, and `layoutOverflow`.
Older version 1 hosts may omit `layoutOverflow`; clients should treat an absent
field as false.
`layoutOverflow` marks a managed tiled window that cannot be staged at its
reported minimum size in the active layout. The manager minimizes it while
preserving workspace membership and its split-tree leaf. `minimized` reports
observed native state separately. Focusing an overflow window in its active
workspace prioritizes it for staging and can replace the focused tile; the
displaced window may then become overflow. Default-ignored windows remain
discoverable but are neither placed nor workspace-hidden.

`desktop.configuredExclusions` is an optional additive version 1 field. An absent
or null field means diagnostics are unavailable; an empty array means no matching
candidates were observed. Entries contain `title`, `processName`, `className`,
`monitorId`, `bounds`, `processNameExcluded`, and `classNameExcluded`. They report
configured process/class exclusions after native visibility, shell/cloak, and
identity checks. They are separate from `desktop.windows`, have no window ID,
and cannot be passed to native-action commands or opted into tiling. They are
not an exhaustive inventory of hidden, shell or inaccessible native windows.
Diagnostic-only changes publish the existing `window.updated` event with a full
committed snapshot; protocol framing and version remain unchanged.

`selectContainer` selects an active tiling-tree node by `nodeId`, or navigates
from a selected/focused tile using `parent`, `firstChild`, `secondChild`, `sibling`,
or `root`. Node targeting and navigation are mutually exclusive. Selection is
logical, not native focus; snapshots expose nullable `selection` containing
`workspaceId` and `nodeId`. `clearSelection` clears it. `setSplit` uses a selected
branch when no explicit `windowId` is supplied. See the [command reference](external-api.md#container-selection).
`ManagerSnapshot.WorkspaceViews` is an optional additive version 1 field containing
`monitorId`/`workspaceId` pairs. Use it as the authoritative monitor view map.
`WorkspaceInfo.MonitorId` and `Active` remain compatibility projections; an
unviewed workspace has `Guid.Empty` and `false`. `WorkspaceInfo.Layout` reports
`split` or `layered`, and `WorkspaceInfo.Position` reports logical desk coordinates.
Older hosts may omit
`WorkspaceViews`; the named-pipe framing and protocol version do not change.
Other newer commands and fields may still require a matching host version.

`focusCycle` accepts `cycleDirection: next|previous` (default `next`), optional
`mode: tiling|floating`, and optional active-workspace name/ID scope. It cycles
visible eligible tiles in tree order, then floating windows in GUID order, and
wraps. `windowId` is an optional starting anchor. Without an anchor/focus, it
starts at the first/last candidate in the primary monitor's active workspace.
For layered workspaces, use `mode: tiling` to cycle pages; native focus raises the
selected window without changing the saved split tree or workspace membership.
Focus is reported through the existing `focus.changed` event, not a new event.
See [focus cycling](external-api.md#filtered-focus-cycling) for scope and error behavior.

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
- `selection.changed`
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