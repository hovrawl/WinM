# Project goal: one shared desk

## Purpose

WinManager will manage one logical desk. Each monitor will show one workspace view onto that desk. A workspace will belong to the desk, not to a monitor.

This document defines the target model. The first system pass now keeps workspace identity independent from monitor views and supports exclusive view transfer. Spatial bounds, workspace layout-rule selection, persistent view assignments, and visualization remain future work. See [the migration plan](desk-model-plan.md) for the status and next phases.

## Confirmed rules

- The desk is the top-level state. It contains workspaces, window membership, workspace positions, layout rules, and monitor views.
- A workspace is a named group of windows on the desk. A workspace can exist without a monitor view.
- A monitor shows one workspace at a time. Any monitor can show any workspace.
- A workspace view is exclusive. If a monitor selects a workspace that another monitor shows, the view transfers to the new monitor. The desk does not show that workspace twice.
- A configured `monitorDeviceName` is an initial placement hint. It does not reclaim a workspace after a view transfer or monitor reconnect.
- A view transfer does not move windows between workspaces or change the workspace's desk position.
- Each workspace selects its own layout rule. Initial rules are columns, rows, and Fibonacci tiles. A rule change recalculates the window layout to fit the workspace view.
- WinManager owns the state and operations. General window-manager compatibility is not a goal.
- The first implementation stage focuses on the data model, rules, commands, and state changes. A full visual desk is a separate stage.

## Space model

The desk uses logical coordinates. A workspace has a position and bounds on the desk. Its member windows form a group inside those bounds. A workspace layout rule places its windows within the area that a monitor view provides.

Desk coordinates do not use monitor pixels, DPI, or a client's zoom level. A monitor view maps the selected workspace layout to that monitor's current work area. The native Windows bounds of a window describe observed placement. They do not define desk position or workspace membership.

A client can zoom out to show the desk. It can show each workspace as a group of its windows. The client can let a user select a workspace view or move a workspace group to a new desk position. A group move changes workspace position. It does not change window membership.

## View transfer policy

The selected workspace leaves its source monitor and appears on the target monitor. The destination monitor no longer shows its previous workspace; that workspace remains on the desk without a view. The manager does not swap the destination workspace to the source monitor.

When a visible workspace transfers, the source monitor receives another workspace that was unviewed before the transfer, selected in stable workspace order. The displaced destination workspace is excluded from that fallback. If no other workspace is available, the manager creates a uniquely named empty workspace. When the selected workspace was already unviewed, the manager assigns it to the target monitor and leaves the displaced workspace unviewed.

An activation command can name `monitorId`. If it omits that field, the manager uses the focused window's observed connected monitor, then the primary monitor with a stable device-name/GUID order. With no connected monitor, activation returns `not_ready`. These rules do not infer monitor ownership from a workspace.

## What the manager owns

The headless system program performs desk operations. It owns workspace membership, view transfer, layout calculation, reflow, native window actions, recovery, commands, and public state events. Persistence of view assignments and spatial workspace data is not implemented yet.

A visual client is not required for the core data model or command-based operation. A user can select a workspace with a shortcut, CLI, or another client. The manager must expose enough state and commands for an integration to display and control the desk.

## Optional visual client

Visual gestures need a user interface, but they do not belong in the desk model. A separate client can provide these views:

- **Desk overview:** a zoomable view that groups each workspace's windows. A user can move a workspace group around the desk and select a workspace to transfer its view.
- **Quick switcher:** a floating modal window with workspace groups at desk-like, uneven positions rather than a uniform list. Arrow keys move selection. Enter text to filter workspace names. Enter selects the workspace for the current monitor view.

Both views use public manager state and commands. The client does not own workspace membership, layout rules, or transfer state. See the [client choices and implementation plan](desk-model-plan.md#visual-client-choices).

## Implementation status

`WorkspaceSet` now keeps workspaces independent from a monitor-to-workspace view map. `ManagerSnapshot.WorkspaceViews` exposes that map; `WorkspaceInfo.MonitorId` and `Active` remain compatibility projections, with `Guid.Empty` and `false` for an unviewed workspace. The manager transfers views, applies the selected source fallback, releases views on monitor removal, and restores view state after native-action failure. `WorkspaceDefinition.MonitorDeviceName` is used as an initial placement hint.

The current native tiling still uses the existing split-tree layout. Logical desk coordinates, workspace bounds, columns/rows/Fibonacci rules, and persistence of transferred views are not part of this first pass. No visual client is included; the overview and quick switcher remain explicit integration work after the manager contract stabilizes.