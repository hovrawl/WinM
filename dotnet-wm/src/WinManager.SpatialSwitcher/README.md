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
Escape hides it; losing focus also hides either popup without ending the tray
process. Only Quit ends the tray app. The app never starts the manager.
`WinManager.SpatialSwitcher` is automatically excluded from
window-management actions by the host, independent of configured ignore lists.

## Appearance and connection

- The tray menu's **Appearance** options choose **System**, **Light**, or **Dark**.
  System is the default and follows the current Windows theme; Light uses the
  warm workspace palette, while Dark preserves the switcher's existing dark colors.
- If the manager is unavailable or the connection drops, the app retries
  connecting every five seconds after each attempt completes. The tray reconnect
  action remains available for an immediate attempt.
- Reconnection only restores the snapshot/subscription. Workspace activation and
  window focus commands are never replayed automatically; inspect fresh state and
  explicitly submit a command if needed.

## Select and activate

- Both switcher popups include the same workspace details control. In the spatial
  switcher it follows the selected workspace card; in the window switcher it stays
  bound to the workspace shown on the monitor captured when the popup opened. Use
  **Use Split** or **Use Layered** to change the workspace layout, or edit the
  layered preferred width cap and horizontal spacing in DIPs and choose
  **Apply settings**. The settings apply to that explicit workspace ID; they are
  disabled in demo mode, while disconnected, or while another command is running.
- Layered width is a preferred maximum percentage from greater than 0 through 100
  of the usable work width. At the 100% default, one page fills the padded work
  area and each added page reduces every page by one horizontal offset while the
  stack remains aligned to the far edge. Horizontal spacing is an integer from 0
  to 256 DIPs; zero overlays pages. Missing layered options show their defaults
  of 100% and 32 DIPs. Smaller explicit width caps may leave space at the far edge.
  These runtime
  changes last until the manager restarts; configure workspace layout options in
  the manager configuration to establish restart defaults.
- Workspace cards are positioned from `WorkspaceInfo.Position`, in logical desk
  units independent of monitor pixels, DPI, and the popup's zoom or size.
- Cards are drawn for every workspace, including unviewed workspaces. Their small
  window silhouettes use observed native bounds relative to each window's monitor
  work area; they do not capture screenshots or invent window contents.
- Search dims nonmatching cards while preserving their map positions and selection.
- Arrow keys select the nearest workspace in that desk direction. Enter or plain
  Space activates the selected workspace on the explicitly selected monitor;
  Space remains available for typing while the search field is focused. Escape
  dismisses the switcher. Clicking a card changes selection but does not activate it.
- A ready manager connection installs a process-wide Win32 low-level keyboard
  hook. `Alt+Tab` opens a separate current-workspace window switcher, showing
  visible windows as larger geometric previews and minimized/overflow windows in
  an inactive row below; repeated presses cycle through both groups. Enter or
  plain Space focuses the selected window and Escape hides the popup. Floating windows are included;
  focusing a manually minimized tiled window adds it to the active layout when it
  fits. If the layout is full, the manager replaces the last focused staged tile;
  the displaced window remains managed as overflow. An individually oversized
  window stays inactive and the manager reports that it cannot fit. Overflow
  windows use the same restore/promotion behavior.
- `Win+Tab` or `Win+Space` opens the spatial workspace switcher. Both popup types
  are placed on the monitor that was foreground before they are shown and keep
  their workspace context bound to that monitor across later manager snapshots,
  even if popup focus changes or the monitor's view is empty. If the native
  foreground monitor cannot be read, the client falls back to the snapshot's
  focused/primary monitor. Window geometry previews use observed bounds only.
- The hook is not installed in `--demo` mode and does not consume shortcuts until
  a live manager snapshot is available. If hook installation fails or the manager
  disconnects, the tray status reports shortcuts unavailable and the key chords
  pass through. `Ctrl+Tab` and unrelated shortcuts remain untouched.
- Activation is sent once. A command failure or disconnect outcome is never
  automatically retried; reconnect and inspect the fresh state before sending
  another command.
- If a snapshot omits or contains invalid workspace positions, the map and
  activation are disabled and the app explains that spatial state is unavailable.
  The client does not invent local positions for older hosts.

The full draggable/zoomable desk overview and persistence of runtime position
changes remain future work. See the [one-desk goal](../../docs/project-goal.md),
[manager migration plan](../../docs/desk-model-plan.md), and
[external API](../../docs/external-api.md).