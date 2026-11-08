# Desk model migration plan

## Goal and scope

Move from monitor-owned workspaces to one shared desk with transferable workspace views. This is a plan only. It does not change code or add a user interface.

The target rules live in [Project goal: one shared desk](project-goal.md). Complete the policy choices below before the implementation fixes behavior.

## Current implementation

- `src/WinManager.Core/WorkspaceSet.cs` stores a `MonitorId` and `Active` flag on each workspace. `Reconcile` binds workspaces to detected monitors and normalizes one active workspace per monitor. Window assignment is a separate map. The manager assigns new windows to workspaces based on their observed monitor.
- `WorkspaceSet.Activate` activates a workspace only within its current monitor group. `MoveWindowToMonitor` finds the active workspace for a monitor.
- `src/WinManager.Core/ManagerEngine.cs` handles `ActivateWorkspace` without a monitor target. It calls `WorkspaceSet.Activate`, then reconciles native state. On native failure, it attempts to restore the previous active workspaces.
- `src/WinManager.Contracts/Commands.cs` defines `WorkspaceDefinition` with an optional monitor device name and `WorkspaceInfo` with monitor ID and active state. `ManagerSnapshot` exposes these workspace records.
- `src/WinManager.Host/ManagerConfiguration.cs` validates the current workspace definitions and shortcut commands. `docs/configuration.md`, `docs/external-api.md`, and `docs/protocol.md` document the current behavior.

These details are the baseline for migration. The `MonitorId` on `WindowInfo` is observed native placement, not a future desk relationship.

## Decisions to make before implementation

1. **Source monitor fallback:** choose the destination's displaced workspace, another unviewed workspace, a configured default, or a new empty workspace.
2. **Destination displacement:** keep the displaced workspace on the desk without a view, or swap it to the source monitor. The target spec does not require an automatic swap.
3. **Request target:** define how a command names the monitor view. Prefer an explicit monitor ID in client requests, with a documented default for shortcuts and older clients.
4. **No-monitor state:** define behavior when the system has no connected monitors. Workspace data and positions must remain valid.
5. **Spatial rules:** choose coordinate units, bounds validation, overlap policy, and defaults for new workspaces. Coordinates must remain independent of monitor pixels, DPI, and client zoom.
6. **Persistence and compatibility:** choose a config/state schema migration, defaults for old files, and a protocol version policy for changed snapshots and commands.

## Phased migration

### 1. Add desk state and invariants

Replace monitor ownership in `WorkspaceSet` with desk state. Keep stable workspace IDs and window membership. Add workspace bounds and a per-workspace layout rule. Add a `WorkspaceView` assignment for each connected monitor.

Enforce these invariants in core state:

- A window belongs to at most one workspace.
- A workspace view appears on at most one monitor.
- A workspace can exist without a view.
- View transfer changes monitor-to-workspace assignments only. It does not change window membership or workspace bounds.
- Observed Windows monitor placement remains separate from workspace membership and desk coordinates.

### 2. Change activation and reconciliation

Refactor `WorkspaceSet.Activate` and `Reconcile` to transfer a view across monitors and apply the chosen source fallback policy. Keep window movement to another workspace distinct from view transfer. Review `MoveWindowToMonitor`. It moves a window to the active workspace on a monitor. Keep that command separate from view transfer.

Update `ManagerEngine` commands to resolve a target monitor view, apply the view transfer, and reconcile window visibility and layout. Preserve the current recovery behavior. If native operations fail, report the failures, restore prior logical view assignments where possible, and publish observed geometry rather than assumed placement.

Update display hotplug and zero-monitor paths. A missing monitor must not delete workspaces or their desk positions. Reconnect must not silently restore monitor ownership unless the selected policy requires it.

### 3. Add workspace layout rules

Keep layout calculation in the system program. Add columns, rows, and Fibonacci rules as workspace-level state. Calculate native rectangles from the current monitor work area and workspace rule. Recalculate when a workspace rule, its bounds, or the view's work area changes.

Keep the core layout input independent of a visual client's zoom and desk coordinates. Decide whether desk bounds affect window layout directly or only workspace placement in the overview. The recommended separation is that desk position affects the overview, while the monitor view's available work area drives native tile rectangles.

### 4. Evolve contracts, configuration, and persistence

Update `src/WinManager.Contracts/Commands.cs` and snapshot records to expose desk state, workspace bounds and rules, monitor view assignments, and view-transfer targets. Add separate commands for view activation and workspace movement when one command mixes those operations.

Update `src/WinManager.Host/ManagerConfiguration.cs` and the persisted state format for workspace positions and layout rules. Define how current `monitorDeviceName` entries migrate. Do not silently reinterpret old workspace-to-monitor bindings as permanent ownership.

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