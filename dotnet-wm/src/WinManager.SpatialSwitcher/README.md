# Spatial switcher

`WinManager.SpatialSwitcher` is a separate, code-only Avalonia popup app for
switching workspace views on **one shared desk**. It consumes snapshots and
commands through the public SDK; it does not create desks or own positions,
workspace membership, monitor views, or layouts.

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

An optional `--pipe PIPE_NAME` selects a local same-user test host. The app never
starts the manager. `WinManager.SpatialSwitcher` is automatically excluded from
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
- Activation is sent once. A failure remains visible without an automatic retry;
  reconnect and inspect the fresh state before sending another command.
- If a snapshot omits or contains invalid workspace positions, the map and
  activation are disabled and the app explains that spatial state is unavailable.
  The client does not invent local positions for older hosts.

The full draggable/zoomable desk overview and persistence of runtime position
changes remain future work. See the [one-desk goal](../../docs/project-goal.md),
[manager migration plan](../../docs/desk-model-plan.md), and
[external API](../../docs/external-api.md).