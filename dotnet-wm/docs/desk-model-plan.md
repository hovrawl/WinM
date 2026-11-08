# Desk model migration plan

## Goal and scope

Move from monitor-owned workspaces to one shared desk with transferable workspace views. The first headless implementation pass is complete. This plan tracks remaining system work and the later visual client; it does not add a user interface.

The target rules live in [Project goal: one shared desk](project-goal.md). Transfer and target-selection policies below are now implemented; spatial, layout, and persistence policies remain open.

## Current implementation

- `src/WinManager.Core/WorkspaceSet.cs` stores workspace identity, window membership, and split trees separately from a monitor-to-workspace view map. A workspace can remain on the desk without a view. `WorkspaceInfo.MonitorId` and `Active` are compatibility projections; unviewed workspaces use `Guid.Empty` and `false`.
- `WorkspaceSet.Reconcile` retains workspaces and memberships when a monitor disconnects. `WorkspaceDefinition.MonitorDeviceName` acts only as an initial placement hint. New windows join the workspace shown by their observed monitor, or the primary view when no matching view exists.
- `WorkspaceSet.Activate` supports a target monitor. `ManagerEngine` accepts optional `monitorId` for `ActivateWorkspace`; otherwise it uses the focused window's observed connected monitor, then a stable primary monitor. Native-action failures restore the prior view map and remove any fallback workspace created by the failed transfer.
- `src/WinManager.Contracts/Commands.cs` exposes `WorkspaceViewInfo` through `ManagerSnapshot.WorkspaceViews`. The optional field is additive to the version 1 snapshot. `MoveToMonitor` still moves a window to the workspace currently shown by a monitor; it is distinct from view transfer.
- `src/WinManager.Host/ManagerConfiguration.cs` validates the optional activation monitor target. The CLI and SDK serialize the existing `ManagerCommand` contract. Configuration, API, and protocol references describe the current view-transfer behavior.

These details are the baseline for migration. The `MonitorId` on `WindowInfo` is observed native placement, not a future desk relationship.

## Policies implemented in the first system pass

- **Source monitor fallback:** use another workspace that was already unviewed, in stable workspace order. Exclude the destination's displaced workspace. Create a uniquely named empty workspace if no other unviewed workspace exists.
- **Destination displacement:** keep the displaced workspace on the desk without a view; do not swap views.
- **Request target:** use explicit `monitorId` when supplied. Otherwise use the focused window's observed connected monitor, then the stable primary monitor.
- **No-monitor state:** retain desk workspaces and membership with no views. Activation returns `not_ready` until a monitor is connected.

## Decisions that remain

- **Spatial rules:** choose coordinate units, bounds validation, overlap policy, and defaults for new workspaces. Coordinates must remain independent of monitor pixels, DPI, and client zoom.
- **Layout rules:** define columns, rows, and Fibonacci tile calculations, including reflow behavior and layout constraints.
- **Persistence and compatibility:** define persisted desk state, view restoration across manager restarts, migration from current configuration, and any future protocol changes.

## Phased migration

### 1. Add desk state and invariants

The first slice is implemented: `WorkspaceSet` keeps stable workspace IDs, window membership, split trees, and monitor views as separate state. The remaining work is to add logical workspace positions and bounds and a per-workspace layout rule.

Enforce these invariants in core state:

- Implemented: a window belongs to at most one workspace.
- Implemented: a workspace view appears on at most one monitor.
- Implemented: a workspace can exist without a view.
- Implemented: view transfer does not change window membership.
- Implemented: observed Windows monitor placement remains separate from workspace membership.
- Remaining: validate desk bounds and define overlap behavior independently of monitor pixels, DPI, and client zoom.

### 2. Change activation and reconciliation

Implemented: `WorkspaceSet.Activate` and `Reconcile` transfer views and retain workspace data during hotplug. Window movement to another workspace remains distinct from view transfer. `MoveWindowToMonitor` still moves a window to the workspace shown by a monitor.

Implemented: `ManagerEngine` resolves explicit or default monitor targets, applies transfers, and reconciles window visibility and layout. If native operations fail, it reports failures, restores prior view assignments, removes a newly created fallback workspace, and publishes observed geometry.

Implemented: disconnected monitors release their views without deleting workspace identity or membership. With zero monitors the desk retains its workspaces and has no views. A reconnect uses current placement rules, not persistent monitor ownership.

### 3. Add workspace layout rules

Keep layout calculation in the system program. Add columns, rows, and Fibonacci rules as workspace-level state. Calculate native rectangles from the current monitor work area and workspace rule. Recalculate when a workspace rule, its bounds, or the view's work area changes.

Keep the core layout input independent of a visual client's zoom and desk coordinates. Decide whether desk bounds affect window layout directly or only workspace placement in the overview. The recommended separation is that desk position affects the overview, while the monitor view's available work area drives native tile rectangles.

### 4. Evolve contracts, configuration, and persistence

Implemented: `src/WinManager.Contracts/Commands.cs` exposes `WorkspaceViews` and an optional monitor target on `ActivateWorkspace`; `MoveToMonitor` remains a separate window-membership operation. Workspace bounds and layout rules remain future contract fields.

Implemented: host validation accepts a nonempty optional activation target. Current `monitorDeviceName` values act as initial placement hints. Persisted workspace positions, layout rules, and transferred view assignments remain future work; the current manager does not save transfers across restarts.

Version protocol changes deliberately. Update `docs/protocol.md` and `docs/external-api.md` only when the new contracts exist. Keep the current version 1 examples marked as current until then. Preserve the current IPC security model and atomic snapshot/event behavior.

### 5. Verify the manager before any visual client

Add or update tests in the core, IPC, and Windows test projects. Cover these cases:

- Transfer a workspace view between monitors. Confirm that no duplicate view exists.
- Confirm that window membership and workspace desk bounds do not change during transfer.
- Confirm source fallback and destination displacement for every selected policy.
- Confirm native failure behavior, rollback limits, and observed geometry reports.
- Add and remove monitors. Test the zero-monitor state. Retain workspace IDs, positions, membership, and layout rules.
- Change columns, rows, and Fibonacci rules. Confirm deterministic reflow and valid rectangles.
- Reject invalid spatial bounds and verify the selected overlap policy.
- Load old and new configuration/state formats and verify documented migration defaults.
- Verify IPC snapshots, event order, command validation, protocol version behavior, and older-client errors.

Run the targeted core, IPC, and Windows tests in a safe interactive environment where required. Do not run native desktop integration tests on a normal managed desktop.

### 6. Add a visual client as an explicit later phase

Do not make a UI a prerequisite for the data model or manager commands. Start UI work only after the manager contracts and core transfer tests are stable.

## Visual client choices

| Choice | Benefits | Costs | Recommendation |
| --- | --- | --- | --- |
| External companion | Keeps the headless manager separate; lets other clients use the same public contracts; supports fast visual experiments. | Needs separate packaging and lifecycle work. | Best first visual client. |
| Bundled separate client | Keeps the process boundary and contracts; offers one installer and coordinated release. | Adds package, update, startup, and lifecycle work. | Consider after the companion proves useful. |
| UI inside the manager host | One process and direct access to state. | Couples UI lifetime and platform concerns to the manager; weakens the headless boundary. | Not recommended. |

The recommended companion can implement the desk overview and quick switcher. The overview can zoom out and show windows in workspace groups. Users can move groups on the desk or select a workspace.

The quick switcher can use a floating modal window with groups at uneven, desk-like positions. Arrow keys move selection. A name filter narrows the choices. Enter activates the selected workspace on the chosen monitor.

The manager must still own all state changes. The companion sends commands through the public IPC contract. It does not maintain a second source of truth.