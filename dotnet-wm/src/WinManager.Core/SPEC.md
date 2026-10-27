---
id: winmanager-core
type: module-design
status: draft
title: Desktop intent and layout engine
parent: winmanager-architecture
depends-on: [winmanager-contracts]
references: [winmanager-goals, winmanager-protocol]
---

# Desktop intent and layout engine

## Responsibility and boundary

Core owns logical desktop intent, command execution, layout calculations, and committed observations. `winmanager-architecture` owns dependency rules and the effect flow.
Core uses `IWindowPlatform` rather than native APIs. It cannot establish physical success without a new desktop capture.

`ManagerEngine` serializes commands and subscriptions. `WorkspaceSet` owns membership, monitor assignments, and per-workspace trees.
`SplitTree` owns topology, geometry, and resize proportions. The separate `DesktopObservation` class supplies observation-only behavior, not a second manager authority.

## Current layout and interaction contract

Each active workspace uses one binary split tree. Horizontal splits produce side-by-side regions. Vertical splits produce stacked regions.
New eligible windows append in GUID order along the second-child branch. Insertion does not use the focused tile.

Untouched splits rebalance by subtree span. Explicit resize ratios survive changes to other leaves where the split survives.

Layout uses physical monitor work areas and DPI-scaled DIP gaps. Tiles require positive dimensions.
Native minimum-size hints do not exclude tiles or override planned tile sizes. Impossible work areas or gaps return `layout_constraints`.

During a native move, Core suppresses placement and visibility changes for the affected window.
A completed drop uses window-center monitor selection and the nearest tile. It transfers to the destination's active workspace and swaps tiles.
The current drop path does not perform edge insertion. Live resize infers moved split boundaries from observed bounds and retains revised proportions.

Ordinary floating windows follow workspace visibility and remain inside destination work areas. Default-ignored overlays do not follow those effects.
Fullscreen, popup, tool, owned, and no-activate windows default to ignored. Per-window mode overrides can opt them into tiling.
Application-hidden and minimized windows remain outside automatic placement and workspace hides.

## Current navigation and workspaces

Exactly one workspace is active per monitor. Explicit window transfer does not activate the destination workspace.
Missing-monitor workspaces migrate to a surviving monitor. Remembered device names permit return after reconnection.

Directional focus uses geometric proximity across eligible observed windows. Directional movement swaps tiles only within the source workspace.
Focus cycling stays inside one active workspace. It wraps through tree leaves, then floating windows in GUID order, with optional mode filtering.

Container selection is logical, not native focus. Only implicit-window `setSplit` acts on a selected branch.
Observed focus changes, deactivated workspaces, and removed nodes clear selection. A surviving node selection can remain after descendant changes or swaps.
There is no selection visualization or general group move/resize contract.

## Failures and consistency

Validation rejects invalid targets before mutation. Native failure does not imply complete rollback of logical intent or physical effects.
Workspace activation and explicit transfers attempt rollback on reconciliation failure. Other tree and mode changes can retain intent after failure.
Unchanged committed state does not increment the revision. Subscriptions begin atomically with a full snapshot under `winmanager-protocol`.

## Known drift and limits

`winmanager-goals` owns the intended UX. The following source behavior does not meet that contract:

- No selectable layout modes or focus-relative insertion exist.
- Drop behavior swaps rather than performs position-sensitive insertion.
- Pause suppresses automatic geometry but still computes and applies workspace visibility. Explicit focus remains available.
- Runtime identities, workspace membership, trees, ratios, and overrides have no persistent session store or restart matching.
- No desktop-wide collection of per-monitor workspaces exists.

Pause claims in `docs/configuration.md` and `docs/external-api.md` describe the desired visibility suspension, not current source behavior.
Those documents remain reference inputs, not additional spec authorities.

## Accepted focused-split contract, not yet implemented

These rules refine `winmanager-goals`. They describe target acceptance, not the current `Reconcile` behavior above.

- Keep a new eligible tile on its observed monitor, in that monitor's active workspace.
- Use an existing focused eligible tile as anchor. Otherwise use that workspace's last observed focused eligible tile.
- If the remembered tile no longer survives, use the last surviving leaf in tree order. Never use the newcomer as its own anchor.
- A first tile fills an empty workspace's usable area, minus outer gaps.
- Split only the anchor region. A single arrival shares it equally with the anchor, minus the inner gap.
- Keep the anchor first. Place the new tile to its right for side-by-side insertion, or below it for stacked insertion.
- A simultaneous arrival batch shares that local region equally with the anchor. Keep the anchor first and use deterministic newcomer order.
- Preserve unrelated regions, orientations, node identities, and deliberate proportions. Equal shares permit one-pixel rounding differences.
- Keep insertion direction sticky per workspace. Changing insertion intent must not rotate an existing container.
- Preserve existing `setSplit` behavior. A separate insertion-direction action exposes its workspace-local value through committed snapshots and events.
- Initial discovery without a saved arrangement uses deterministic equal columns. Subsequent discoveries use local insertion.
- A window that returns from floating or minimized state uses normal insertion. Do not reserve an absent tile's old slot.
- Closure collapses the empty branch without global rebalance. An untouched local equal-share group redistributes only inside its retained region.
- Preserve deliberate ratios on surviving resized splits. Do not infer permission to rebalance unrelated ancestors from their lack of resize history.
- Do not force native focus on arrival or closure. Record observed focus and keep anchor history local to its workspace.

Recognized transient tools follow the product policy in `winmanager-goals`. Their focus must not erase the last eligible tile anchor.
The focused-split slice preserves current tool exclusions. Broader classifier and owner-workspace lifecycle changes require a separate design.
Existing geometry failure, visibility recovery, and semantic-exclusion safety contracts remain in force.

**Unverified risk:** a lost native move-end notification can retain placement suppression. Periodic reconciliation does not time out move tracking.
Fake-platform tests do not prove native focus acceptance, application resize compliance, or desktop interaction timing.
