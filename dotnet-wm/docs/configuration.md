# Configuration and operation

The host runs on the logged-in user's Windows desktop, not as a Session-0
service. It requests normal privilege and per-monitor-v2 DPI awareness.
Default configuration is created at `%LOCALAPPDATA%\WinManager\config.json`.
Use `--config ABSOLUTE_PATH` for an existing alternate JSON file. Changes take
effect on restart; live reload and YAML import are deferred.

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
  An optional `monitorDeviceName` on a workspace uses the device name from a
  snapshot, for example `\\.\DISPLAY1`. Workspaces without an explicit binding
  initially use the Windows primary display (at desktop origin `0,0`), not random
  monitor IDs. Each other display gets an active workspace named after its device,
  so `status` showing `1` on one monitor and `\\.\DISPLAY3` on another is normal;
  both active workspaces are tiled independently.
  Missing displays are handled through reconciliation; detached monitor bindings
  migrate to surviving displays and return to their original device when it
  reconnects, retaining workspace identity, window assignments, and split trees.
- Gaps are DIPs in `[0,256]`, converted per-monitor to physical pixels.
- Process/class ignores are case-insensitive exact names, not regex rules.
  Fullscreen, popup (`WS_POPUP`), tool (`WS_EX_TOOLWINDOW`), owned, and no-activate
  windows are ignored semantically by default, without excluding their entire
  application class. They remain in snapshots but are neither placed nor hidden.
  `setMode`/`toggleMode` can explicitly tile an individual observed window; hard
  process/class exclusions cannot be overridden this way.
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
in an active workspace receives a tile according to the split tree. Application
minimum-size hints do not override tile widths or heights or cause windows to be
skipped. The native placement layer requests the exact layout size, even below
those hints; an application that independently refuses programmatic resizing
can still constrain its own actual bounds.

Default horizontal splits form equal-width columns (within one rounding pixel),
not recursively halved columns. Untouched vertical splits likewise divide
height equally. Adding/removing windows rebalances untouched splits; explicit
command or drag-resize proportions are retained for surviving splits. Gaps are
DPI-scaled and subtracted before distributing space. Split orientations, window
order, and workspace assignments are preserved, including mixed-axis layouts.
Per-window startup widths and saving/restoring split proportions across host
restarts are not currently configuration options; a fresh host starts with equal
shares unless changed through runtime commands or resizing.

Invalid work areas or insufficient room for one-pixel tiles and configured gaps
return `layout_constraints` rather than placing invalid rectangles. Startup and
subsequent action failures are logged under `%LOCALAPPDATA%\WinManager\Logs`;
unchanged diagnostics are deduplicated until the diagnostic state changes.
Ordinary floating windows are kept inside destination work areas during monitor
transfers. Default-ignored overlays and fullscreen windows are left untouched,
unless explicitly opted into tiling. Application-minimized windows remain excluded
from placement and workspace hiding regardless of an override. See the
[window defaults and override reference](external-api.md#window-defaults-and-manual-overrides).

Before a workspace hide, the helper durably records HWND/process identity and
original show state and acknowledges it. If the helper cannot start or register,
the manager refuses unsafe hiding. It monitors the host's actual process lifetime
and restores identity-matched windows after a crash. Graceful exit restores
manager-hidden windows before helper completion and final event draining. Stale
journals are retried only when their owning host is no longer alive.

Shutdown restores manager-hidden visibility, not every pre-tiling rectangle.
Application-hidden windows are never spuriously restored. Do not delete recovery
journals while a host/helper is running. If restoration fails, preserve the
journals, investigate logs, and restart in the same user session.

Logon registration is explicit through CLI `install-startup`/`uninstall-startup`,
using only the current user's Run entry. No service, elevation, scheduled task,
or visualization process is required. Real logon and mixed-DPI/hotplug hardware
validation should accompany deployment to a new machine.