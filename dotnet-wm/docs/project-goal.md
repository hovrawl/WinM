# Project goal: one shared desk

## Purpose

WinManager will manage one logical desk. Each monitor will show one workspace view onto that desk. A workspace will belong to the desk, not to a monitor.

This document defines the target model. The current program still uses monitor-bound workspaces. See [the migration plan](desk-model-plan.md) for a phased path from the current model.

## Confirmed rules

- The desk is the top-level state. It contains workspaces, window membership, workspace positions, layout rules, and monitor views.
- A workspace is a named group of windows on the desk. A workspace can exist without a monitor view.
- A monitor shows one workspace at a time. Any monitor can show any workspace.
- A workspace view is exclusive. If a monitor selects a workspace that another monitor shows, the view transfers to the new monitor. The desk does not show that workspace twice.
- A view transfer does not move windows between workspaces or change the workspace's desk position.
- Each workspace selects its own layout rule. Initial rules are columns, rows, and Fibonacci tiles. A rule change recalculates the window layout to fit the workspace view.
- WinManager owns the state and operations. General window-manager compatibility is not a goal.
- The first implementation stage focuses on the data model, rules, commands, and state changes. A full visual desk is a separate stage.

## Space model

The desk uses logical coordinates. A workspace has a position and bounds on the desk. Its member windows form a group inside those bounds. A workspace layout rule places its windows within the area that a monitor view provides.

Desk coordinates do not use monitor pixels, DPI, or a client's zoom level. A monitor view maps the selected workspace layout to that monitor's current work area. The native Windows bounds of a window describe observed placement. They do not define desk position or workspace membership.

A client can zoom out to show the desk. It can show each workspace as a group of its windows. The client can let a user select a workspace view or move a workspace group to a new desk position. A group move changes workspace position. It does not change window membership.

## View transfer policy

The target rule is exclusive transfer. The selected workspace leaves its source monitor and appears on the target monitor. The destination monitor no longer shows its previous workspace. That displaced workspace remains on the desk and remains available from any monitor.

Each connected monitor retains one workspace view after transfer. This spec does not yet select a source fallback policy or decide if the transfer swaps views. Candidate policies include:

1. Swap views. The source monitor shows the workspace that the destination monitor showed before the transfer.
2. Show another unviewed workspace, such as the source monitor's most recently viewed workspace.
3. Show a configured fallback workspace, or create an empty workspace if no fallback is available.

The implementation plan must compare these choices before it fixes one. A swap is an option, not a requirement. The plan must also define which monitor receives a request when a command does not name a target monitor. The command context must not infer ownership from a workspace.

## What the manager owns

The headless system program can perform all desk operations. It owns the desk model, workspace membership, view transfer, layout calculation, reflow, native window actions, state persistence, recovery, commands, and public state events.

A visual client is not required for the core data model or command-based operation. A user can select a workspace with a shortcut, CLI, or another client. The manager must expose enough state and commands for an integration to display and control the desk.

## Optional visual client

Visual gestures need a user interface, but they do not belong in the desk model. A separate client can provide these views:

- **Desk overview:** a zoomable view that groups each workspace's windows. A user can move a workspace group around the desk and select a workspace to transfer its view.
- **Quick switcher:** a floating modal window with workspace groups at desk-like, uneven positions rather than a uniform list. Arrow keys move selection. Enter text to filter workspace names. Enter selects the workspace for the current monitor view.

Both views use public manager state and commands. The client does not own workspace membership, layout rules, or transfer state. See the [client choices and implementation plan](desk-model-plan.md#visual-client-choices).

## Implementation status

The rules above describe the target, not current behavior. The current `WorkspaceSet` assigns workspaces to monitors and permits one active workspace per monitor. Current configuration, snapshots, commands, and protocol docs describe that implementation. The migration plan will update those contracts before a visual client uses the desk model.