# HomeDesk debug console

`HomeDesk.DebugUi` is a separate, code-only Avalonia management application.
It inspects and controls **one shared desk** through `HomeDesk.Client`. It does
not create desks or own a second copy of manager state. Named entries such as
`Code` and `Chat` are workspaces (window groups), not separate desks. Monitors
only show exclusive workspace views onto that same desk.

## Configuration

The console's **Edit config** action reads and saves the host-selected JSON file,
showing its path, revision, and current validation status. The editor keeps invalid
file text available for repair, validates replacements with the host's strict
configuration rules, detects stale revisions, and requires confirmation before
discarding unsaved text during reload. It writes atomically and never changes the
running manager configuration; restart the manager to apply saves. **Use current
workspace settings** stages names, layouts, layered options, and positions from
the latest snapshot while preserving unrelated configuration fields and monitor
placement hints. IPC editing is limited to 128 KiB; larger configurations can be
edited directly on disk.

## Run

From the repository root, against an already-running manager in the same Windows
user/session:

```powershell
dotnet run --project .\src\HomeDesk.DebugUi\HomeDesk.DebugUi.csproj
```

To inspect the screen safely without connecting to a manager or manipulating any
desktop windows:

```powershell
dotnet run --project .\src\HomeDesk.DebugUi\HomeDesk.DebugUi.csproj -- --demo
```

An optional `--pipe PIPE_NAME` selects a local same-user test host. The default is
the SDK's current-user/session pipe. The companion never starts the manager.

## Inspect and control

- The single desk contains all workspace groups, including unviewed workspaces.
  Every connected monitor view is shown even if its workspace has no windows.
- Each workspace displays its current `Split` or `Layered` layout. Select a
  workspace to choose either layout; layered workspaces also provide previous/next
  page controls that cycle focus through the visible tiled windows. Workspace
  coordinates are displayed and editable as logical desk positions; this does
  not resize or move native windows. The configured position is used as the restart
  default because runtime desk-state persistence is not implemented.
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
  The `HomeDesk.DebugUi` process is automatically exempt through a built-in
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

HomeDeskManager is the separate [companion app](../HomeDeskManager/README.md).
The draggable/zoomable full desk overview and automatic persistence of runtime
desk state remain future work. Save workspace layouts and positions explicitly
through the configuration editor when they should seed a later manager restart.