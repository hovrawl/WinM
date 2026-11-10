# Project goal: one shared desk

## Purpose

WinManager manages exactly one logical desk. There is no desk list, desk switching, or separate desk per monitor. Each monitor shows one workspace view onto that same desk. A workspace belongs to the desk, not to a monitor.

This document defines the target model. The system keeps workspace identity independent from monitor views, supports exclusive view transfer, manager-owned logical workspace positions, and per-workspace split and layered layouts. Current tiling keeps every eligible window managed when minimum sizes exceed available space. Workspace bounds, additional layout rules, persistent runtime desk state, and the full desk overview remain future work. The separate debug console and spatial switcher are available; see [the migration plan](desk-model-plan.md) for status and next phases.

## Confirmed rules

- The desk is the top-level state. It contains workspaces, window membership, workspace positions, layout rules, and monitor views.
- A workspace is a named group of windows on the desk. A workspace can exist without a monitor view.
- A monitor shows one workspace at a time. Any monitor can show any workspace.
- A workspace view is exclusive. If a monitor selects a workspace that another monitor shows, the view transfers to the new monitor. The desk does not show that workspace twice.
- A configured `monitorDeviceName` is an initial placement hint. It does not reclaim a workspace after a view transfer or monitor reconnect.
- A view transfer does not move windows between workspaces or change the workspace's desk position.
- Each workspace selects its own layout rule. The current choices are split and layered; columns, rows, and Fibonacci tiles remain possible later rules. A rule change recalculates the window layout to fit the workspace view.
- When not all windows fit at their reported minimum sizes, the system stages a deterministic fitting subset and keeps every other window managed as layout overflow. Overflow windows are minimized rather than squeezed below minimum size; focusing an overflow window in the active workspace can replace the focused tile.
- WinManager owns the state and operations. General window-manager compatibility is not a goal.
- The first implementation stage focuses on the data model, rules, commands, and state changes. A full visual desk is a separate stage.

## Space model

The desk uses logical coordinates. Each workspace has a manager-owned position; workspace bounds and drag placement are not implemented yet. Its member windows form a group at that position. A workspace layout rule places its windows within the area that a monitor view provides.

Desk coordinates do not use monitor pixels, DPI, or a client's zoom level. A monitor view maps the selected workspace layout to that monitor's current work area. The native Windows bounds of a window describe observed placement. They do not define desk position or workspace membership.

A client can zoom out to show the desk. It can show each workspace as a group of its windows. The debug console can edit a workspace's logical coordinates; a future draggable overview can move a workspace group. A position change does not change window membership or move/resize native windows.

## View transfer policy

The selected workspace leaves its source monitor and appears on the target monitor. The destination monitor no longer shows its previous workspace; that workspace remains on the desk without a view. The manager does not swap the destination workspace to the source monitor.

When a visible workspace transfers, the source monitor receives another workspace that was unviewed before the transfer, selected in stable workspace order. The displaced destination workspace is excluded from that fallback. If no other workspace is available, the manager creates a uniquely named empty workspace. When the selected workspace was already unviewed, the manager assigns it to the target monitor and leaves the displaced workspace unviewed.

An activation command can name `monitorId`. If it omits that field, the manager uses the focused window's observed connected monitor, then the primary monitor with a stable device-name/GUID order. With no connected monitor, activation returns `not_ready`. These rules do not infer monitor ownership from a workspace.

## What the manager owns

The headless system program performs desk operations. It owns workspace membership, view transfer, workspace positions, layout calculation, reflow, native window actions, recovery, commands, and public state events. Configuration can seed positions for restart defaults; runtime position and view assignments are not persisted across manager restarts.

A visual client is not required for the core data model or command-based operation. A user can select a workspace with a shortcut, CLI, or another client. The manager must expose enough state and commands for an integration to display and control the desk.

## Optional visual client

Visual gestures need a user interface, but they do not belong in the desk model. A separate client can provide these views:

- **Spatial switcher:** a separate popup with workspace groups at their manager-owned, desk-like positions. Search dims nonmatches without rearranging them; arrow keys navigate by coordinates and Enter transfers the selected view to an explicitly selected monitor.
- **Full desk overview:** a future zoomable view that groups workspace windows and supports moving workspace groups around the desk.

Both views use public manager state and commands. The client does not own workspace membership, layout rules, or transfer state. See the [client choices and implementation plan](desk-model-plan.md#visual-client-choices).

## Implementation status

`WorkspaceSet` now keeps workspaces independent from a monitor-to-workspace view map. `ManagerSnapshot.WorkspaceViews` exposes that map; `WorkspaceInfo.MonitorId` and `Active` remain compatibility projections, with `Guid.Empty` and `false` for an unviewed workspace. The manager transfers views, applies the selected source fallback, releases views on monitor removal, and restores view state after native-action failure. `WorkspaceDefinition.MonitorDeviceName` is used as an initial placement hint. `WorkspaceInfo.Position` exposes logical coordinates; `WorkspaceDefinition.Position` seeds them, deterministic defaults are assigned when omitted, and `setWorkspacePosition` changes them without native window actions.

The current native tiling offers the split-tree layout and the layered page stack. Both respect window minimum sizes and preserve every workspace member; layered mode overlays pages with a bounded cascade and uses focus cycling to bring pages forward. `layoutOverflow` is exposed in snapshots and the CLI status view; Windows minimizes overflow windows with crash recovery, and focus can promote an overflow window by replacing the focused tile. Workspace bounds, columns/rows/Fibonacci rules, and persistence of runtime position/layout/view changes are not implemented.

The separate [HomeDesk debug console](../src/HomeDesk.DebugUi/README.md) and [HomeDeskManager](../src/HomeDeskManager/README.md) are the visual slices. They inspect or select workspaces on the single desk and send public manager commands; neither creates another desk or owns membership. Safely identifiable configured exclusions are exposed as read-only diagnostics, separate from actionable windows. The draggable full desk overview remains later integration work.