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
  snapshot, for example `\\.\DISPLAY1`. Missing displays are handled through
  reconciliation; detached monitor bindings migrate to surviving displays.
- Gaps are DIPs in `[0,256]`, converted per-monitor to physical pixels.
- Process/class ignores are case-insensitive exact names, not regex rules.
- Shortcut modifier bits: Alt `1`, Control `2`, Shift `4`, Win `8`, NoRepeat
  `16384`. The example uses Control+Shift+NoRepeat. `virtualKey` uses Windows
  virtual-key codes; reserved F12 is rejected. Duplicate keys and invalid command
  arguments fail validation; native registration conflicts stop startup safely.
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
Periodic enumeration closes event/discovery races. Layouts honor observed
minimum sizes; impossible constraints return `layout_constraints` rather than
placing invalid rectangles. Floating windows are kept inside destination work
areas during monitor transfers. Native fullscreen and minimized windows are
excluded from automatic placement and workspace hiding.

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