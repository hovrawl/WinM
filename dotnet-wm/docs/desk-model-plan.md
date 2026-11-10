# Desk model migration plan

## Goal and scope

Move from monitor-owned workspaces to exactly one shared desk with transferable workspace views. The headless manager now supports logical workspace positions and per-workspace split/layered layouts; separate Avalonia debug-console and spatial-switcher apps are available. This plan tracks remaining system work and the future full desk overview.

The target rules live in [Project goal: one shared desk](project-goal.md). Transfer, target-selection, logical workspace positions, split/layered layout, and page-focus policies below are implemented; workspace bounds, additional layouts, and persistent runtime state remain open.

## Current implementation

- `src/HomeDesk.Core/WorkspaceSet.cs` stores workspace identity, window membership, and split trees separately from a monitor-to-workspace view map. A workspace can remain on the desk without a view. `WorkspaceInfo.MonitorId` and `Active` are compatibility projections; unviewed workspaces use `Guid.Empty` and `false`.
- `WorkspaceSet.Reconcile` retains workspaces and memberships when a monitor disconnects. `WorkspaceDefinition.MonitorDeviceName` acts only as an initial placement hint. New windows join the workspace shown by their observed monitor, or the primary view when no matching view exists.
- `WorkspaceSet.Activate` supports a target monitor. `ManagerEngine` accepts optional `monitorId` for `ActivateWorkspace`; otherwise it uses the focused window's observed connected monitor, then a stable primary monitor. Native-action failures restore the prior view map and remove any fallback workspace created by the failed transfer.
- `src/HomeDesk.Contracts/Commands.cs` exposes `WorkspaceViewInfo` through `ManagerSnapshot.WorkspaceViews`. The optional field is additive to the version 1 snapshot. `MoveToMonitor` still moves a window to the workspace currently shown by a monitor; it is distinct from view transfer.
- `src/HomeDeskSpace/ManagerConfiguration.cs` validates the optional activation monitor target. The CLI and SDK serialize the existing `ManagerCommand` contract. Configuration, API, and protocol references describe the current view-transfer behavior.
- `SplitTree.Layout` stages only windows whose reported minimum width and height fit. It retains the full tree and reports overflow IDs; `ManagerEngine` exposes `WindowInfo.LayoutOverflow`, minimizes overflow, and supports promoting one through the existing `focus` command. The Windows recovery helper restores manager-minimized overflow on graceful exit or host failure. CLI status and the separate Avalonia inspector expose affected windows.
- `WorkspaceLayout` is per-workspace state. `layered` is the default for new or unspecified workspaces; `split` remains available as an explicit choice. Layered places every individually fitting tiled window in a large horizontal page stack and reports only individually oversized pages as overflow. The saved split tree and workspace membership are unchanged, and tiling `focusCycle` brings the selected page forward.
- `WorkspaceDefinition.Layout` configures the restart default. `setWorkspaceLayout` changes runtime state and immediately reflows; a native action failure reports its error and restores the previous layout selection. The inspector exposes layout selection and previous/next page controls.
- `WorkspaceInfo.Position` is manager-owned logical desk state. `WorkspaceDefinition.Position` seeds a configured workspace; omitted positions receive deterministic four-column defaults. `setWorkspacePosition` publishes `workspace.updated` without native window actions. Runtime changes are not persisted across restarts.

These details are the baseline for migration. The `MonitorId` on `WindowInfo` is observed native placement, not a future desk relationship.

## Policies implemented in the first system pass

- **Source monitor fallback:** use another workspace that was already unviewed, in stable workspace order. Exclude the destination's displaced workspace. Create a uniquely named empty workspace if no other unviewed workspace exists.
- **Destination displacement:** keep the displaced workspace on the desk without a view; do not swap views.
- **Request target:** use explicit `monitorId` when supplied. Otherwise use the focused window's observed connected monitor, then the stable primary monitor.
- **No-monitor state:** retain desk workspaces and membership with no views. Activation returns `not_ready` until a monitor is connected.

## Decisions that remain

- **Spatial bounds:** decide workspace bounds and overlap policy for future drag placement. Implemented point coordinates are finite and bounded to ±100000 desk units, with stable defaults independent of monitor pixels, DPI, and client zoom.
- **Additional layout rules:** define columns, rows, and Fibonacci tile calculations, including reflow behavior and layout constraints. Split and layered layouts are implemented.
- **Persistence and compatibility:** define persisted desk state, view restoration across manager restarts, migration from current configuration, and any future protocol changes.

## Phased migration

### 1. Add desk state and invariants

The first slice is implemented: `WorkspaceSet` keeps stable workspace IDs, window membership, split trees, per-workspace split/layered choices, logical positions, and monitor views as separate state. Workspace bounds and additional layout rules remain future work.

Enforce these invariants in core state:

- Implemented: a window belongs to at most one workspace.
- Implemented: a workspace view appears on at most one monitor.
- Implemented: a workspace can exist without a view.
- Implemented: view transfer does not change window membership.
- Implemented: observed Windows monitor placement remains separate from workspace membership.
- Implemented: validate logical positions independently of monitor pixels, DPI, and client zoom.
- Remaining: add workspace bounds and define overlap behavior for drag placement.

### 2. Change activation and reconciliation

Implemented: `WorkspaceSet.Activate` and `Reconcile` transfer views and retain workspace data during hotplug. Window movement to another workspace remains distinct from view transfer. `MoveWindowToMonitor` still moves a window to the workspace shown by a monitor.

Implemented: `ManagerEngine` resolves explicit or default monitor targets, applies transfers, and reconciles window visibility and layout. If native operations fail, it reports failures, restores prior view assignments, removes a newly created fallback workspace, and publishes observed geometry.

Implemented: disconnected monitors release their views without deleting workspace identity or membership. With zero monitors the desk retains its workspaces and has no views. A reconnect uses current placement rules, not persistent monitor ownership.

### 3. Add workspace layout rules

Implemented baseline: the existing split-tree layout respects each window's minimum size, deterministically admits fitting leaves while retaining the full saved tree, and reports the rest as overflow. The manager minimizes overflow windows, exposes logical `layoutOverflow` separately from observed `minimized`, retries native failures, restores manager-minimized windows when capacity returns, and can promote an overflow window by replacing the focused tile. Application-minimized windows remain excluded and are not auto-restored.

Keep layout calculation in the system program. Split and layered calculations are implemented as workspace-level state. Add columns, rows, and Fibonacci rules as later choices. Calculate native rectangles from the current monitor work area and workspace rule. Recalculate when a workspace rule, its bounds, or the view's work area changes.

Keep the core layout input independent of a visual client's zoom and desk coordinates. Decide whether desk bounds affect window layout directly or only workspace placement in the overview. The recommended separation is that desk position affects the overview, while the monitor view's available work area drives native tile rectangles.

### 4. Evolve contracts, configuration, and persistence

Implemented: `src/HomeDesk.Contracts/Commands.cs` exposes `WorkspaceViews`, per-workspace `WorkspaceLayout`, `DeskPosition`, `SetWorkspaceLayout`, `SetWorkspacePosition`, and an optional monitor target on `ActivateWorkspace`; `MoveToMonitor` remains a separate window-membership operation. Workspace bounds and persistent runtime desk-state fields remain future contract work.

Implemented: host validation accepts workspace layout and optional position defaults and shortcut commands. Current `monitorDeviceName` values act as initial placement hints. Layout and position defaults are configured per workspace; runtime layout/position changes and transferred view assignments are not persisted across restarts.

Version protocol changes deliberately. Update `docs/protocol.md` and `docs/external-api.md` only when the new contracts exist. Keep the current version 1 examples marked as current until then. Preserve the current IPC security model and atomic snapshot/event behavior.

### 5. Verify the manager before any visual client

Add or update tests in the core, IPC, and Windows test projects. Cover these cases:

- Transfer a workspace view between monitors. Confirm that no duplicate view exists.
- Confirm that window membership and workspace desk bounds do not change during transfer.
- Confirm source fallback and destination displacement for every selected policy.
- Confirm native failure behavior, rollback limits, and observed geometry reports.
- Confirm partial fit, no-fit overflow, stable saved split trees, focus promotion/swap, capacity-growth restoration, manual-minimize distinction, native minimization failure/retry, and recovery.
- Confirm layered cascade bounds at mixed DPI/negative coordinates, preservation of minimum sizes, restoration of split overflow when layered pages fit, and next/previous focus wrap.
- Add and remove monitors. Test the zero-monitor state. Retain workspace IDs, positions, membership, and layout rules.
- Change split/layered layouts and later columns, rows, and Fibonacci rules. Confirm deterministic reflow and valid rectangles.
- Reject invalid spatial bounds and verify the selected overlap policy.
- Load old and new configuration/state formats and verify documented migration defaults.
- Verify IPC snapshots, event order, command validation, protocol version behavior, and older-client errors.

Run the targeted core, IPC, and Windows tests in a safe interactive environment where required. Do not run native desktop integration tests on a normal managed desktop.

### 6. Add visual clients without changing single-desk ownership

Implemented visual slices: `HomeDesk.DebugUi` is a code-only Avalonia debug console using the shared manager session and SDK; it shows monitor views, workspaces, positions, window state and read-only configured-exclusion diagnostics with explicit controls. `HomeDeskManager` is a separate code-only Avalonia popup that uses desk positions for its map, directional navigation, search dimming, and explicit monitor-targeted activation. Both offer safe demo mode. See the [debug console guide](../src/HomeDesk.DebugUi/README.md) and [HomeDeskManager guide](../src/HomeDeskManager/README.md).

There is exactly one desk, not a collection of desks. UI groups represent workspaces and monitor views on that shared desk. Keep the manager headless and authoritative. The full draggable/zoomable desk overview remains later work; it must consume manager coordinates and must not invent a separate desk model in the client.

## Visual client choices

| Choice | Benefits | Costs | Recommendation |
| --- | --- | --- | --- |
| External companion | Keeps the headless manager separate; lets other clients use the same public contracts; supports fast visual experiments. | Needs separate packaging and lifecycle work. | Best first visual client. |
| Bundled separate client | Keeps the process boundary and contracts; offers one installer and coordinated release. | Adds package, update, startup, and lifecycle work. | Consider after the companion proves useful. |
| UI inside the manager host | One process and direct access to state. | Couples UI lifetime and platform concerns to the manager; weakens the headless boundary. | Not recommended. |

The separate companion apps now implement the debug console and spatial switcher. The future full overview can zoom out and show windows in workspace groups, including an explicit overflow list that explains why a window is minimized and lets the user focus it to replace a staged tile. Users can move groups on the desk or select a workspace.

The spatial switcher uses a popup with groups at their manager-owned desk positions. Arrow keys move selection spatially, search dims nonmatching cards without moving them, and Enter activates on the explicitly chosen monitor.

The manager must still own all state changes. The companion sends commands through the public IPC contract. It does not maintain a second source of truth.