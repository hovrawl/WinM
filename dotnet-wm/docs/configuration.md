# Configuration and operation

The host runs on the logged-in user's Windows desktop, not as a Session-0
service. It requests normal privilege and per-monitor-v2 DPI awareness.
Default configuration is created at `%LOCALAPPDATA%\WinManager\config.json`.
Use `--config ABSOLUTE_PATH` for an existing alternate JSON file. The Debug
console's **Edit config** action reads and saves this same host-selected file;
clients cannot choose a different path. Saving validates the complete JSON
against host rules, writes a same-directory temporary file, and atomically
replaces the configuration. The manager does not hot-reload: restart it for saved
settings to take effect. You can also edit the file directly while the manager is
running, then reload it in the editor; save detects a file revision change and
requires an explicit reload instead of overwriting newer content. Reloading after
unsaved edits requires confirmation. Configuration editing over IPC is limited
to 128 KiB; larger files can be edited directly and the manager accepts files up
to the 1 MiB protocol configuration limit.

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
manager restarts. Runtime workspace names, layouts, layered options, and logical
positions can be explicitly captured into the configuration editor and saved;
this does not persist live window membership, monitor views, or native window
geometry. See [the project goal](project-goal.md) and
[the migration plan](desk-model-plan.md) for remaining desk-state work.

New workspaces use the horizontal `layered` page-stack layout by default. Set
`layout` to `split` explicitly to use the split-tree layout; existing configuration
values are preserved.

Example configuration (default manager shortcuts are enabled unless opted out):

```json
{
  "workspaces": [{ "name": "1", "layout": "split", "position": { "x": 0, "y": 0 } }, { "name": "2", "layout": "layered", "position": { "x": 520, "y": -80 }, "layeredLayoutOptions": { "windowWidthRatio": 1.0, "horizontalOffset": 32 } }, { "name": "3" }],
  "innerGap": 8,
  "outerGap": 8,
  "ignoredProcessNames": ["example.exe"],
  "ignoredClassNames": ["ExampleToolWindow"],
  "enableDefaultShortcuts": true,
  "touchpad": { "enabled": true },
  "monitorOrder": ["\\\\.\\DISPLAY2", "\\\\.\\DISPLAY1"],
  "shortcuts": [
    { "modifiers": 16390, "virtualKey": 80, "command": { "kind": "pause", "paused": true } },
    { "modifiers": 16390, "virtualKey": 82, "command": { "kind": "pause", "paused": false } },
    { "modifiers": 16390, "virtualKey": 84, "command": { "kind": "toggleMode" } },
    { "modifiers": 16390, "virtualKey": 49, "command": { "kind": "activateWorkspace", "workspaceName": "1" } },
    { "modifiers": 16390, "virtualKey": 50, "command": { "kind": "activateWorkspace", "workspaceName": "2" } },
    { "modifiers": 16390, "virtualKey": 76, "command": { "kind": "setWorkspaceLayout", "workspaceName": "2", "layout": "layered" } }
  ],
  "inputCapacity": 256,
  "maximumClients": 16,
  "outputCapacity": 128,
  "logMaximumBytes": 2097152,
  "logFileCount": 4
}
```

`shortcuts` contains optional custom bindings; matching generated key combinations
are replaced by custom bindings rather than registered twice.

- Names are unique, case-insensitive, nonblank strings (maximum 64 characters).
  An optional `monitorDeviceName` uses the device name from a snapshot, for example
  `\\.\DISPLAY1`, as an initial placement hint. Unbound entries fill currently
  unassigned monitor views in configuration order. When there are more monitors
  than available configured workspaces, the manager creates a uniquely named
  workspace for each remaining view. An unviewed workspace retains its identity,
  window assignments, and split tree when a monitor is removed.
- `monitorOrder` optionally lists up to ten unique monitor device names from the
  snapshot (for example `\\.\DISPLAY2`). Alt+Number uses this list as fixed
  numbered slots. A disconnected configured monitor keeps its slot reserved;
  connected monitors not listed are appended in stable primary/device-name/GUID
  order. If the list is omitted or empty, all connected monitors use that stable
  order. Alt+Number focuses the nearest eligible window on that monitor; it does
  not activate another workspace view. An empty monitor returns `not_found`.
- Each workspace can set `layout` to `split` or `layered`; omitted values default
  to `layered`. Optional `layeredLayoutOptions` tune `windowWidthRatio` (default
  `1.0`, greater than zero and at most `1`) and `horizontalOffset` (default `32`
  DIPs, from `0` to `256`). Layered pages use the configured share of usable work
  width, remain full-height, and overlap on one horizontal line. The DPI-scaled
  horizontal offset is compressed when a larger stack would exceed the available
  width; a zero offset completely overlays pages. Only a page whose own minimum
  size exceeds the work area overflows. Runtime layout/option changes last until
  the manager restarts; configure these values to select restart defaults. On a
  Layered workspace, focus changes only the native Z-order: the selected page is
  brought to the front, then its immediately adjacent left and right pages are
  shown underneath it, followed by the next pair outward, and so on. Equal-distance
  ties follow saved page order. For example, focusing page 4 in a seven-page stack
  gives the front-to-back order `4, 3, 5, 2, 6, 1, 7`. Page bounds and horizontal
  positions remain unchanged, and the workspace tree is not reordered. With no
  eligible focused page, the last successful stack order is retained.
- Each workspace can seed an optional logical `position` with finite `x` and `y`
  coordinates in `[-100000,100000]`. If omitted, the manager assigns stable
  defaults in workspace creation order, four columns at 480 desk units by 360
  desk units. Coordinates are independent of monitor pixels, DPI and client zoom.
  The runtime `setWorkspacePosition` command and debug console update the snapshot
  and publish `workspace.updated` without moving or resizing native windows;
  configure a position to restore it after a manager restart.
- `activateWorkspace` accepts an optional `monitorId`. Without it, the manager
  targets the focused window's observed connected monitor, then the primary
  monitor in stable device-name/GUID order. If no monitor is connected, activation
  returns `not_ready`. The destination's previous workspace remains unviewed; a
  workspace transferred from another monitor leaves its source monitor on another
  unviewed workspace or on a new empty workspace. This operation changes a view,
  not workspace membership. `moveToMonitor` remains a separate window-membership
  command.
- `setWorkspaceLayout` requires a workspace ID or configured name and a `layout`
  value (`split` or `layered`). It reflows the active view and preserves workspace
  membership and the saved split tree. A native reflow failure reports the error
  and restores the previous layout selection. While the manager is paused, reflow
  waits until resume. `focusCycle` with `mode: "tiling"`
  and `cycleDirection: "next"` or `"previous"` navigates the layered page stack.
- Gaps are DIPs in `[0,256]`, converted per-monitor to physical pixels.
- Process/class ignores are case-insensitive exact names, not regex rules.
  The `WinManager.DebugUi` and `WinManager.SpatialSwitcher` companion processes
  are always excluded, even when `ignoredProcessNames` is empty. They are never
  tiled, workspace-hidden, or minimized by the manager; their read-only
  process-exclusion diagnostics cannot be opted into management by editing the
  configured lists or issuing `setMode`.
  Fullscreen, popup (`WS_POPUP`), tool (`WS_EX_TOOLWINDOW`), owned, and no-activate
  windows are ignored semantically by default, without excluding their entire
  application class. They remain in snapshots but are neither placed nor hidden.
  `setMode`/`toggleMode` can explicitly tile an individual observed window; hard
  process/class exclusions cannot be overridden this way.
  Safely identifiable configured-excluded candidates appear in the optional
  snapshot `desktop.configuredExclusions` and the [debug companion](../src/HomeDesk.DebugUi/README.md),
  separately from actionable windows. This diagnostic is not an exhaustive native
  window list; shell, hidden/cloaked and inaccessible candidates are still filtered.
  Change these configuration lists and restart the host to include a configured-excluded
  window; the built-in companion exemption remains in effect.
- `enableDefaultShortcuts` defaults to `true`. Alt+`1`…`9` and Alt+`0` focus
  monitor slots 1–10. Alt+Shift+`1`…`9` and Alt+Shift+`0` move the focused window
  to workspace slots 1–10 in configured workspace order; unavailable slots have
  no generated binding. Set `enableDefaultShortcuts` to `false` to disable these
  generated bindings while retaining custom `shortcuts`.
- Alt+H/J/K/L focuses the nearest window left/down/up/right, respectively, in the
  active workspace, falling back to the nearest window on a monitor in that
  direction. Alt+Shift+H/J/K/L performs the corresponding spatial move: it floats
  and positions the focused window beside a local neighbor, or moves it to the
  neighboring monitor's viewed workspace when there is no local neighbor.
- `touchpad.enabled` defaults to `true`. The Host registers its own native
  Precision Touchpad input source; it does not depend on Windows Settings
  shortcut mappings or change registry settings. Three-finger left/right maps
  inversely to `Focus` (swipe left focuses right); four-finger swipes map directly
  to the existing `Move` command, swapping the focused tiled window with its
  directional neighbor one position in the stack. Stack edges do not wrap. Three-finger
  vertical gestures are not Host focus commands. If shell gesture suppression is
  unverified, Windows may also perform its configured four-finger action. Keyboard
  shortcuts remain available, and setting this to
  `false` disables HomeDeskSpace-native gesture input. Restart HomeDeskSpace and
  HomeDeskManager after changing it. The carousel can also be opened or closed
  from HomeDeskManager with `Ctrl+Alt+Space`; keyboard shortcuts remain usable
  when native touchpad input is disabled or unsupported.
  The related `touchpad.completeThreeFingerSwipesOnFirstLift` option defaults to
  `true`. A settled three-finger swipe is published on the first finger lift, based
  on movement accumulated before that lift; the remaining reports from that contact
  sequence are ignored until all fingers are up. Set it to `false` to retain
  completion after all fingers lift. Raw Input cannot stop the touchpad or Windows
  from producing further input or inertia, and this option does not suppress
  Windows' configured shell gestures.
  Contact recognition and exclusive shell-gesture ownership are separate. Once
  contacts are observed, SpatialSwitcher routes its own carousel gestures and the
  switcher reverses the raw two-finger horizontal delta before scrolling the
  carousel. The Host can dispatch three-finger horizontal focus; Windows may also
  perform its configured action, so the Host logs a warning while suppression is
  unverified.
  Four-finger `MoveSpatial` remains blocked until exclusive suppression is verified.
  Neither process changes Windows settings or the registry.
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

Layered workspaces place every fitting tiled window in an overlapping horizontal
page stack. `windowWidthRatio` is a preferred maximum page width relative to the
usable work width (default `1.0`, subject to each fitting window's minimum), while
`horizontalOffset` sets the preferred start-to-start spacing in DIPs. At the
default 100% width, one page fills the padded work area; each additional page
reduces every page's width by one offset so the stack still reaches the far edge.
If the spacing would make any fitting page narrower than its minimum, the manager
reduces the uniform offset to the largest value that preserves all fitting pages.
An explicitly smaller width ratio caps each page and can leave unused space at
the far edge. Pages retain full work-area height. A window over the work area's
own width or height remains overflow; stack size alone does not overflow otherwise
fitting pages. Focus cycling raises the selected page without changing workspace
membership or tree order.

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

HomeDeskSpace runs without a visible main window and places a HomeDeskSpace icon
in the Windows notification area. HomeDeskManager has its own tray menu, which
opens the switcher and its general settings window. While a connected
HomeDeskManager instance is running, HomeDeskSpace hides its tray icon; it restores
the icon when the last manager connection closes, including after a crash. Choose
**Quit HomeDeskSpace** from the host icon's context menu to request graceful
shutdown; the host then completes the normal recovery-protected restore and
cleanup path. The manager settings window is available only through the
HomeDeskManager tray menu. Appearance changes apply immediately; host settings are
saved to the existing configuration file and require restarting HomeDeskSpace.