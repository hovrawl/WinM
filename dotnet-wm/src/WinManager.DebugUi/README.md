# One-desk debug/control companion

`WinManager.DebugUi` is a separate, code-only Avalonia Fluent desktop application.
It inspects and controls **one shared desk** through `WinManager.Client`. It does
not create desks or own a second copy of manager state. Named entries such as
`Code` and `Chat` are workspaces (window groups), not separate desks. Monitors
only show exclusive workspace views onto that same desk.

## Run

From the repository root, against an already-running manager in the same Windows
user/session:

```powershell
dotnet run --project .\src\WinManager.DebugUi\WinManager.DebugUi.csproj
```

To inspect the screen safely without connecting to a manager or manipulating any
desktop windows:

```powershell
dotnet run --project .\src\WinManager.DebugUi\WinManager.DebugUi.csproj -- --demo
```

An optional `--pipe PIPE_NAME` selects a local same-user test host. The default is
the SDK's current-user/session pipe. The companion never starts the manager.

## Inspect and control

- The single desk contains all workspace groups, including unviewed workspaces.
  Every connected monitor view is shown even if its workspace has no windows.
- Windows show physical monitor versus workspace membership, native exclusion
  flags, explicit tiling overrides, visibility, minimization, overflow and minimum
  size. The selected-window display identifies the target by title and ID and
  shows its current mode. Redundant mode actions are disabled, while `Set tiling`
  remains available to explicitly override semantic exclusions. `Set floating`
  excludes an observable window from tiling. Explicitly returning to floating
  restores its last observed pre-tiling floating bounds once, clamped to its
  workspace view's current work area; it does not continually constrain user
  movement beyond the existing floating-window clamping. Command feedback names
  the requested mode and target window during sending, success, and failure.
- Select a workspace and monitor to show that workspace there. Select a window
  and workspace to change membership. `Focus / promote` can promote an overflow
  window in its active workspace. All these actions send explicit selected IDs,
  never implicitly the currently focused native window.
- Configured process/class exclusions appear separately as read-only diagnostics.
  They have no actionable window ID. Edit the host's `ignoredProcessNames` or
  `ignoredClassNames` configuration and restart it to include configured exclusions.
  The `WinManager.DebugUi` process is automatically exempt through a built-in
  safety rule independent of `ignoredProcessNames`: the manager never tiles,
  workspace-hides, or minimizes its windows. It can appear in the read-only
  process exclusion diagnostics, but editing exclusion lists cannot opt it in.
  These entries cover safely identifiable visible top-level candidates, not shell,
  cloaked, inaccessible, or every otherwise non-observable native window.
- Pause/resume and rescan/redraw use the same public commands as the CLI. Errors
  are displayed. Commands are never replayed after timeouts or disconnects;
  reconnect explicitly to obtain a fresh snapshot and inspect the actual outcome.
  Old snapshots remain visible but are labeled stale and all actions are disabled.
- Older hosts can omit exclusion diagnostics or view assignments. The companion
  marks diagnostics unavailable and uses legacy workspace view projections only
  when the authoritative `WorkspaceViews` field is absent.

Debug builds enable the installed `avalonia-mcp` inspection bridge through
`AvaloniaMcp.Diagnostics`. Release builds do not start that diagnostic server.
Do not use development inspection tools to invoke live manager controls during
safe UI verification; use `--demo` or a separate fake-platform test pipe.

This is an inspector, not the spatial desk overview or quick switcher. Logical
desk positions, drag placement, zoom, and the modal switcher remain explicit
later work; the companion does not invent spatial coordinates for them.