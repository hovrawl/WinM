# Configuration and operation

The host runs on the logged-in user's Windows desktop, not as a Session-0
service. It requests normal privilege and per-monitor-v2 DPI awareness.
Default configuration is created at `%LOCALAPPDATA%\WinManager\config.json`.
Use `--config ABSOLUTE_PATH` for an existing alternate JSON file. Changes take
effect on restart; live reload and YAML import are deferred.

`monitorDeviceName` is an initial placement hint, not workspace ownership. A
matching connected display receives that workspace during reconciliation. Other
unbound workspaces fill remaining monitor views in configuration order; a monitor
with no available workspace gets a uniquely named empty workspace. A hint for a
missing display remains available until that display appears, but a completed
view transfer is not undone by a later reconcile.

Each connected monitor shows one workspace. A workspace can have no view. The
current view map is exposed in `ManagerSnapshot.WorkspaceViews`; the older
`WorkspaceInfo.MonitorId` and `Active` fields are projections, with `Guid.Empty`
and `false` for an unviewed workspace. View assignments are not saved across
manager restarts. See [the project goal](project-goal.md) and
[the migration plan](desk-model-plan.md) for the remaining spatial and persistence work.

Example (shortcuts are disabled in the generated defaults):

```json
{
  "workspaces": [{ "name": "1" }, { "name": "2" }, { "name": "3" }],
  "innerGap": 8,
  "outerGap": 8,
  "ignoredProcessNames": ["example.exe"],
  "ignoredClassNames": ["ExampleToolWindow"],
  "shortcuts": [
    { "modifiers": 16390, "virtualKey": 80, "command": { "kind": "pause", "paused": true } },
    { "modifiers": 16390, "virtualKey": 82, "command": { "kind": "pause", "paused": false } },
    { "modifiers": 16390, "virtualKey": 84, "command": { "kind": "toggleMode" } },
    { "modifiers": 16390, "virtualKey": 49, "command": { "kind": "activateWorkspace", "workspaceName": "1" } },
    { "modifiers": 16390, "virtualKey": 50, "command": { "kind": "activateWorkspace", "workspaceName": "2" } }
  ],
  "inputCapacity": 256,
  "maximumClients": 16,
  "outputCapacity": 128,
  "logMaximumBytes": 2097152,
  "logFileCount": 4
}
```

- Names are unique, case-insensitive, nonblank strings (maximum 64 characters).
  An optional `monitorDeviceName` uses the device name from a snapshot, for example
  `\\.\DISPLAY1`, as an initial placement hint. Unbound entries fill currently
  unassigned monitor views in configuration order. When there are more monitors
  than available configured workspaces, the manager creates a uniquely named
  workspace for each remaining view. An unviewed workspace retains its identity,
  window assignments, and split tree when a monitor is removed.
- `activateWorkspace` accepts an optional `monitorId`. Without it, the manager
  targets the focused window's observed connected monitor, then the primary
  monitor in stable device-name/GUID order. If no monitor is connected, activation
  returns `not_ready`. The destination's previous workspace remains unviewed; a
  workspace transferred from another monitor leaves its source monitor on another
  unviewed workspace or on a new empty workspace. This operation changes a view,
  not workspace membership. `moveToMonitor` remains a separate window-membership
  command.
- Gaps are DIPs in `[0,256]`, converted per-monitor to physical pixels.
- Process/class ignores are case-insensitive exact names, not regex rules.
  The `WinManager.DebugUi` companion process is always excluded, even when
  `ignoredProcessNames` is empty. It is never tiled, workspace-hidden, or minimized
  by the manager; its read-only process-exclusion diagnostic cannot be opted into
  management by editing the configured lists or issuing `setMode`.
  Fullscreen, popup (`WS_POPUP`), tool (`WS_EX_TOOLWINDOW`), owned, and no-activate
  windows are ignored semantically by default, without excluding their entire
  application class. They remain in snapshots but are neither placed nor hidden.
  `setMode`/`toggleMode` can explicitly tile an individual observed window; hard
  process/class exclusions cannot be overridden this way.
  Safely identifiable configured-excluded candidates appear in the optional
  snapshot `desktop.configuredExclusions` and the [debug companion](../src/WinManager.DebugUi/README.md),
  separately from actionable windows. This diagnostic is not an exhaustive native
  window list; shell, hidden/cloaked and inaccessible candidates are still filtered.
  Change these configuration lists and restart the host to include a configured-excluded
  window; the built-in companion exemption remains in effect.
- Shortcut modifier bits: Alt `1`, Control `2`, Shift `4`, Win `8`, NoRepeat
  `16384`. The example uses Control+Shift+NoRepeat. `virtualKey` uses Windows
  virtual-key codes; reserved F12 is rejected. Duplicate keys and invalid command
  arguments fail validation; native registration conflicts stop startup safely.
  The Control+Shift+T example toggles the focused window between tiled and untiled,
  including opting a default-ignored window into tiling. Overrides last until the
  window disappears or the host restarts; they are not saved in configuration.
- Shortcuts use exactly the same typed handlers as IPC. They are simple single
  bindings, not chords or binding modes. Rapid key bursts may exhaust the native
  signal queue; choose NoRepeat to avoid autorepeat bursts. IPC overload returns
  `busy`; lagging event clients disconnect and must obtain a fresh snapshot.
- Input capacity: `1..4096`; clients: `1..64`; client output: `8..1024` messages.
  Log files: `2..10`, with each maximum size `65536..20971520` bytes.

Unknown/duplicate JSON properties, invalid enums, null lists, duplicate names,
oversized documents, and unsafe limits are rejected before native initialization.
No shortcut automatically starts a shell or any arbitrary executable.

# Lifetime and recovery

The hidden native notification window receives display/work-area changes.
Periodic enumeration closes event/discovery races. Every eligible tiled window
in an active workspace is managed, but a tile is staged only when its rectangle
meets that window's reported minimum width and height. The manager keeps the full
split tree and workspace membership, marks windows that do not fit with
`layoutOverflow`, and minimizes them instead of squeezing them below their
minimums. If a subset fits, it prefers windows already staged and then admits
other fitting windows deterministically.

Default horizontal splits form equal-width columns (within one rounding pixel),
not recursively halved columns. Untouched vertical splits likewise divide
height equally. Adding/removing windows rebalances untouched splits; explicit
command or drag-resize proportions are retained for surviving splits. Gaps are
DPI-scaled and subtracted before distributing space. Split orientations, window
order, and workspace assignments are preserved, including mixed-axis layouts.
Per-window startup widths and saving/restoring split proportions across host
restarts are not currently configuration options; a fresh host starts with equal
shares unless changed through runtime commands or resizing.

Invalid work areas or gaps that cannot produce valid pixel rectangles return
`layout_constraints`; windows that do not meet their minimum size remain in
overflow, including when none fit. `layoutOverflow` in the snapshot describes the
layout decision, while `minimized` reports the observed native state, so a failed
minimize remains explicit and retryable. Focus an overflow window in its active
workspace to promote it and replace the focused tile when possible; the displaced
tile can become overflow. The native taskbar keeps minimized windows discoverable,
and the CLI status view lists overflow titles. Startup and subsequent action
failures are logged under `%LOCALAPPDATA%\WinManager\Logs`; unchanged diagnostics
are deduplicated until the diagnostic state changes.
Ordinary floating windows are kept inside destination work areas during monitor
transfers. Default-ignored overlays and fullscreen windows are left untouched,
unless explicitly opted into tiling. Application-minimized windows remain excluded
from placement and workspace hiding regardless of an override. Manager-minimized
overflow windows remain in their workspace tree and are restored when they fit
again or are promoted by focus. See the
[window defaults and override reference](external-api.md#window-defaults-and-manual-overrides).

Before a workspace hide or overflow minimize, the helper durably records
HWND/process identity and original show state and acknowledges it. If the helper
cannot start or register, the manager refuses the native action. It monitors the
host's actual process lifetime and restores identity-matched windows after a crash.
Graceful exit restores manager-hidden windows and manager-minimized overflow
windows before helper completion and final event draining. Stale journals are
retried only when their owning host is no longer alive.

Shutdown restores manager-hidden visibility and the original show state of
manager-minimized overflow windows, not every pre-tiling rectangle. Application-
minimized windows are never spuriously restored. Do not delete recovery journals
while a host/helper is running. If restoration fails, preserve the journals,
investigate logs, and restart in the same user session.

Logon registration is explicit through CLI `install-startup`/`uninstall-startup`,
using only the current user's Run entry. No service, elevation, scheduled task,
or visualization process is required for the current manager. Real logon and mixed-DPI/hotplug hardware
validation should accompany deployment to a new machine.