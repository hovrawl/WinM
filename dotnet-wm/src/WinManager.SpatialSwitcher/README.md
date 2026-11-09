# Spatial switcher

`WinManager.SpatialSwitcher` is a separate, code-only Avalonia tray app for
switching windows and workspace views on **one shared desk**. It consumes
snapshots and commands through the public SDK; it does not create desks or own
positions, workspace membership, monitor views, or layouts.

## Run

With the manager already running in the same Windows user/session:

```powershell
dotnet run --project .\src\WinManager.SpatialSwitcher\WinManager.SpatialSwitcher.csproj
```

Use `--demo` to render sample workspaces without connecting to a manager or
activating native windows:

```powershell
dotnet run --project .\src\WinManager.SpatialSwitcher\WinManager.SpatialSwitcher.csproj -- --demo
```

An optional `--pipe PIPE_NAME` selects a local same-user test host. The app starts
with no switcher window; use the tray icon menu to show the window or workspace
switcher, reconnect to the manager, or quit. Closing the switcher or pressing
Escape hides it; only Quit ends the tray process. The app never starts the
manager. `WinManager.SpatialSwitcher` is automatically excluded from
window-management actions by the host, independent of configured ignore lists.

## Select and activate

- Workspace cards are positioned from `WorkspaceInfo.Position`, in logical desk
  units independent of monitor pixels, DPI, and the popup's zoom or size.
- Cards are drawn for every workspace, including unviewed workspaces. Their small
  window silhouettes use observed native bounds relative to each window's monitor
  work area; they do not capture screenshots or invent window contents.
- Search dims nonmatching cards while preserving their map positions and selection.
- Arrow keys select the nearest workspace in that desk direction. Enter activates
  the selected workspace on the explicitly selected monitor; Escape dismisses the
  switcher. Clicking a card changes selection but does not activate it.
- A ready manager connection installs a process-wide Win32 low-level keyboard
  hook. `Alt+Tab` summons/cycles windows in the workspace captured from the latest
  snapshot; `Win+Tab` summons/cycles workspace cards. Hold `Shift` to reverse;
  Enter focuses the selected window or activates the selected workspace. The
  popup can be hidden while either shortcut summons it again. Window previews
  include floating and overflow windows; focusing an overflow window uses the
  manager's restore/promotion behavior.
- The hook is not installed in `--demo` mode and does not consume shortcuts until
  a live manager snapshot is available. If hook installation fails or the manager
  disconnects, the tray status reports shortcuts unavailable and the key chords
  pass through. Ctrl+Tab and unrelated shortcuts remain untouched.
- Activation is sent once. A failure remains visible without an automatic retry;
  reconnect and inspect the fresh state before sending another command.
- If a snapshot omits or contains invalid workspace positions, the map and
  activation are disabled and the app explains that spatial state is unavailable.
  The client does not invent local positions for older hosts.

The full draggable/zoomable desk overview and persistence of runtime position
changes remain future work. See the [one-desk goal](../../docs/project-goal.md),
[manager migration plan](../../docs/desk-model-plan.md), and
[external API](../../docs/external-api.md).