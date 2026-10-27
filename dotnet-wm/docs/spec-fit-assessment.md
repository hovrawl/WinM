# Spec fit assessment

## Scope and authority

This report compares the current implementation with [product requirements](../../goal-and-requirements.md).
It records evidence and verification gaps. It does not replace the spec graph or declare incomplete features implemented.
The inspected runtime baseline is commit `2a1460b`. No runtime changes form part of this assessment.

The owner subsequently accepted the focused-split contract and added first-class transient-tool requirements. The rows below still describe current source, not target completion.

The written specs remain draft. Approval of the assessment process does not automatically activate every specification.

## Status meanings

- **Implemented / safe-tested:** source and successful non-interactive assertions support the behavior. This does not prove native desktop behavior.
- **Implemented / under-verified:** source implements the behavior, but relevant desktop or acceptance evidence remains incomplete.
- **Partial / different:** useful mechanisms exist, but the observable behavior does not meet the product requirement.
- **Missing:** no current implementation supplies the requirement.
- **Design unresolved:** the owner identified the concept, but no complete acceptance contract exists.

## Layout and direct manipulation

| Requirement | Fit | Source and evidence | Missing acceptance evidence |
| --- | --- | --- | --- |
| Per-monitor workspace layout mode and focused-split default | Missing | `WorkspaceSet` owns per-workspace `SplitTree` objects. `Commands.cs` exposes no layout-mode choice or per-workspace mode state. | Workspace-local mode selection, default selection, observation, and switch semantics. |
| Insert beside the focused tile | Partial / different | `SplitTree.Reconcile` sorts new IDs and calls `Append`. `WorkspaceSet.ReconcileTrees` supplies no insertion anchor. `LayoutTests.ReconcileExcludesIneligibleWindowsAndIsIndependentOfInputOrder` covers current order, not focused insertion. | Prior-focus anchor when a new app takes focus, monitor scope, fallback, batch discovery, direction, and local proportions. |
| Automatic rows/columns, master-and-stack, scrolling | Missing as selectable modes | `FullWorkspaceLayoutTests.ColumnsTileEveryWindowAtEqualWidthsDespiteMinimumHints` and `RowsTileEveryWindowAtEqualHeightsAndReflowWhenOneCloses` verify equal-share tree layouts. They do not establish separate modes. | Mode-specific insertion, sizing, navigation, and conversion between modes. |
| Edge insertion and center swap on drop | Partial / different | `ManagerEngine.ReflowDrop` uses window-center monitor selection and nearest planned tile. `DraggingTests.DragMovesFreelyAndDropSwapsTilesWithoutExplicitRedraw` and `DropOnAnotherMonitorTransfersWorkspaceAndReflowsBothMonitors` verify swaps and transfer. | Drop coordinates, edge/center regions, insertion topology, cancellation, and visual feedback. |
| Mouse resize retains deliberate proportions | Implemented / safe-tested | `SplitTree.ResizeToBounds` and `ManagerEngine.ReflowResize`. `ResizingTests.ResizeReflowsNeighborsDuringInteractionAndKeepsGeometryOnRelease` and `FinalResizeSampleOnReleaseUpdatesLayoutWithoutAnIntermediateNotification` verify fake-platform results. | Reproduction against ordinary native applications, interrupted interaction, and lost move-end recovery. |
| Stable arrangement after closure | Partial / different | `SplitTree.Prune` collapses removed siblings. `LayoutTests.ReconcilePreservesRatiosAndIdsAndCollapsesRemovedSiblings` retains resized ancestors. `FullWorkspaceLayoutTests.NewColumnsRebalanceOnlyUntouchedSplitsAndKeepResizedProportions` verifies equal-share rebalance. | Focused-split closure must preserve unrelated regions, including untouched ancestor splits. Current rebalance tests are not proof of that contract. |
| Existing floating behavior and semantic exclusions | Implemented / safe-tested | `ManagerEngine.ApplyModes`, `WorkspaceSet.Visibility`, and `WindowInfo.IsIgnored`. `WindowSemanticsTests.IgnoredWindowsRemainObservableAndUntouchedUntilExplicitlyTiled` and `ClassificationChangesReflowAutomaticallyAndManualModesSurviveRescans`. | Representative real applications, popup/owner lifecycle, and interaction with each additional mode. |
| First-class transient tools without routine per-app rules | Partial / different | `WindowsDesktopObserver.ReadWindow` exposes style bits and an ownership boolean. `WindowStyleClassificationTests.ClassifiesIndependentNativeBitsAndOwnerWithoutNativeCalls` verifies flags, not a multi-signal classifier or owner relationship. Current ignored tools do not follow owner-workspace visibility. | Conservative role recognition, owner-workspace association, ordinary-window false positives, ephemeral lifecycle, and no size-only classification. This is a separate slice. |
| Coherent keyboard and mouse controls | Partial / different | Commands and shortcuts converge on the engine. Container selection and split changes have Core/IPC coverage. `setSplit` changes existing containers, not a general insertion preference. | Preserve current orientation controls and add a separate workspace-local insertion action with observable state. Verify both input paths and legacy command compatibility. |

## Workspaces and continuity

| Requirement | Fit | Source and evidence | Missing acceptance evidence |
| --- | --- | --- | --- |
| Independent active workspace per monitor | Implemented / under-verified | `WorkspaceSet.Activate` changes only workspaces assigned to the target monitor. `WorkspaceTests.ConfiguredDeviceNamesResolveAndEveryMonitorHasOneActiveWorkspace` and `WorkspaceManagerTests.BothMonitorsRetileTheirOriginalWorkspacesAfterPrimaryDisplayReconnects` cover model behavior. | Explicit regression for a switch leaving the other monitor's activation and geometry unchanged. Physical hotplug and mixed-DPI checks remain unverified. |
| Restore surviving arrangements after host restart | Missing | Workspace/node IDs and mode overrides are runtime state. No arrangement store or cross-launch window matcher exists. `ClientIntegrationTests.SdkFreshConnectionAfterRestartUsesNewInstance` verifies reconnection identity, not arrangement restoration. | Window matching, duplicates, missing windows, changed displays, corrupt state, and restore without launching apps. |
| Exit restores visibility/show state but keeps geometry | Implemented / under-verified | `WindowsDesktopObserver.RestoreAsync` and recovery mechanisms restore protected visibility. `WorkspaceManagerTests.SwitchingHidesWithTrackedIntentAndShutdownRestores` covers Core intent. Isolated production-host tests exist. | Native graceful restoration and an explicit assertion that exit does not revert the latest tile rectangles. |

## Pause, configuration, and desktop safety

| Requirement | Fit | Source and evidence | Missing acceptance evidence |
| --- | --- | --- | --- |
| Pause suspends automatic geometry and visibility but allows deliberate commands | Partial / different | `ManagerEngine.ReconcileAsync` guards placements with `paused`, but always computes workspace visibility. Explicit focus runs, while explicit move/resize/split update intent without geometry application. `ManagerTests.PauseStopsGeometryActionsAndResumeRelayouts` checks geometry only. | Automatic show/hide suppression, physical effects of deliberate commands, paused discovery, exact resume results, and failure cases. |
| Transactional live configuration reload | Missing | `ManagerConfiguration.Load` validates startup JSON only. `HostConfigurationTests.InvalidJsonConfigurationIsRejected` does not establish live reload. Native `RegisterShortcuts` releases the old set before replacement and cannot restore it after conflict. | Full candidate commit or previous controls/configuration restoration after both validation and application failures. Preserve arrangement on either outcome. |
| Lightweight companion with a reload action | Missing; visual design unresolved | No companion project exists. CLI `status` and the interactive menu are terminal clients, not the companion. | Companion design, reload result/failure presentation, and public reload operation. |
| Safe hides and restoration after host failure | Implemented / under-verified | Windows visibility requires acknowledged helper protection before hides. The helper checks exact host lifetime and window identity. Isolated tests include `RegistrationWithoutAcknowledgmentNeverHidesWindow` and `ProductionHostCrashRestoresHiddenWindowButNotUnrelatedOrMismatchedIdentity`. | Rerun native crash/helper-death/restoration tests in a disposable interactive session. Safe tests alone do not establish crash recovery. |
| Honest native failure and observed-state reporting | Implemented / safe-tested | `ManagerEngine` captures again after effects. `ManagerTests.FailedGeometryAndFocusNeverBecomeCommittedPhysicalState` verifies fake failure behavior. | Native focus restriction and application resize refusal checks. An accepted asynchronous request is not proof of physical success. |
| Public query, commands, and change observation | Implemented / safe-tested | `PipeServer`, `WinManagerClient`, and subscriptions. `PipeServerTests.SubscribeSnapshotPrecedesResponseAndCommandsInterleaveCommittedEvents`, `RegistrationIsAtomicWithConcurrentChanges`, and `ClientIntegrationTests.SdkDemultiplexesSnapshotCommandsAndEventsAndUnsubscribes`. | Public contracts for insertion intent, layout modes, persistence, and reload when those features acquire approved designs. |

## Explicit experiments

Grouped desktop contexts, Fibonacci layout, canvas layout, and companion feedback remain **design unresolved**.
Their absence does not invalidate the independent-workspace baseline. They are not implicit dependencies of focused insertion.
The headless engine and public API remain the integration boundary for any companion experiment.

## Accepted focused-split scenarios

The authoritative contract is in [the Core spec](../src/WinManager.Core/SPEC.md#accepted-focused-split-contract-not-yet-implemented).
The following fixtures are acceptance targets, not new tests or claims of a pass. Numeric examples omit gaps unless stated.

| Case | Given and action | Expected evidence |
| --- | --- | --- |
| FS-01 Initial discovery | Several existing normal windows, no saved arrangement. Reverse the enumeration order in a second fresh engine. | Equal-width deterministic columns in both engines. No claim that GUID order reproduces launch history. |
| FS-02 Empty workspace | First eligible tile arrives in an empty active workspace. | One tile fills the work area minus outer gaps. |
| FS-03 Local insertion | A has 600 pixels, unrelated D has 400. Anchor A and introduce B. | A=300, B=300, D=400. Retain D's node, region, orientation, and outer split ratio. |
| FS-04 Batch insertion | The same setup discovers B and C in one reconciliation, in either enumeration order. | A=B=C=200, D=400. A remains first. Newcomer order is deterministic. |
| FS-05 Direction intent | Choose stacked insertion, then introduce windows on successive captures. | Each local insertion uses height. Existing containers do not rotate. Another workspace retains its own choice. |
| FS-06 Focus race and tools | B already owns native focus when first observed. In another run, a recognized tool owns focus before B appears. | Use the last observed eligible tile in that workspace. Neither B nor the tool becomes its own insertion anchor. Tool focus does not split the layout. |
| FS-07 Fallback and re-entry | Remove the remembered anchor. Restore a minimized window or explicitly return a floating window to tiling. | Use the last surviving leaf, or the empty-workspace rule. Do not reserve or resurrect an old vacant slot. |
| FS-08 Closure | Close B in the untouched local A/B/C group from FS-04. | A=C=300, D=400. Collapse only the removed branch and redistribute within the retained local region. |
| FS-09 Deliberate proportions | Resize mixed-axis groups, then insert or close a tile in another group. | Retain every surviving unrelated resized ratio and region. Assert boundaries, not only window order. |
| FS-10 Monitor isolation | Open a normal window on monitor B while global focus was on A. | Insert in B's active workspace. A's active workspace, visibility, region, and tree remain unchanged. |
| FS-11 Stable reconciliation | Reconcile the same observed desktop repeatedly after convergence. | No duplicate leaves, insertion, refocus, or revision change without another state change. |
| FS-12 Native effects | Application refuses placement, or native focus changes independently during arrival/closure. | Report actual geometry/focus and diagnostic failures. Do not fabricate physical success or force arrival/closure focus. |
| FS-13 Controls and observation | Use existing container orientation and new insertion intent through typed commands and shortcut forwarding. | Preserve old `setSplit` selection behavior. Observe workspace-local insertion intent through snapshots/events with no unwanted rotation. |
| FS-14 Geometry boundaries | Use negative coordinates, mixed DPI, gaps, and insufficient usable area. | Deterministic valid rectangles and rounding. Impossible geometry produces a structured diagnostic rather than invalid placement. |
| FS-15 Tool regressions | Open a recognized ephemeral tool while its owner is tiled, then dismiss it and open a normal window. | Owner layout and eligible anchor remain stable. Do not claim that this test proves broader tool detection or owner-lifecycle handling. |

The transient-tool slice needs a separate corpus. Include equivalent ordinary/tool windows from the same application class, small ordinary windows, and owner/style changes.
Add owner-workspace, owner disappearance, and uncertain-signal scenarios after that slice has an approved lifecycle design.
Do not close tools on behalf of an application merely because they are transient. Tool closure policy has no approved design here.

## Reproduced safe baseline

From `dotnet-wm`:

```powershell
dotnet test WinManager.sln --filter 'Category!=IsolatedInteractive' --verbosity minimal
```

- Core: **146 passed**.
- IPC: **124 passed, 1 failed**.
- Windows non-interactive: **47 passed**.
- Total: **317 passed, 1 failed, 0 skipped** among selected tests.

The failure is `StartupRegistrationTests.InstallUpdateAndIdempotentUninstallAffectOnlyDisposableValue`.
Its fixture creates temporary files under `src/WinManager.Cli/startup <GUID>`, so path length grows with checkout depth.
In this checkout, the quoted host/config command contains 300 characters. `StartupRegistration.Write` rejects it above 260 before registry assertions execute.

A narrow repair uses a short writable disposable root and a combined-command length budget. Keep spaces, unique registry names, cleanup, and explicit overlong-command coverage.
Do not remove or weaken the product assertion to make the fixture pass. Inspect a bounded fixture-path repair as separate work.

The test suite uses disposable HKCU startup entries. Non-interactive does not mean free of external effects.

The production host and isolated desktop tests did not run. Physical keyboard, display hardware, real logon, and external-credential IPC checks remain unverified.
AOT deployment is also unproven. Source-generated protocol JSON does not establish whole-host/helper AOT support.

## Ordered work and exit gates

1. **Reliable baseline:** separately repair the path-sensitive fixture and specify/correct pause. Require targeted regression tests and the complete safe suite. Do not fold either fix into focused insertion.
2. **Focused-split behavior:** implement against the accepted Core contract and FS scenarios, with a separate insertion-intent control. Cover local insertion, direction, resize, closure, exclusions, focus races, and unchanged unrelated workspaces.
3. **First-class transient tools:** independently design conservative classification and owner-workspace lifecycle behavior. Keep recognized tools safe in focused insertion while this broader model remains incomplete.
4. **Direct-manipulation drops:** define edge/center hit areas and native event inputs, then verify insertion, swaps, cancellation, and cross-monitor transfer.
5. **Continuity and configuration:** design persistence/window matching and live reload as separate changes. Reload includes native-resource failure rollback, not just JSON validation.
6. **Additional modes and experiments:** define mode-specific behavior after the split workflow provides a stable control and validation foundation.

Every behavior change has three gates: automated contract tests, isolated native evidence, and owner confirmation of the actual workflow.
Keep durable specs aligned with each accepted implementation. A green safe suite cannot replace native validation or owner UX approval.

## Evidence navigation

Source files above are under `src/WinManager.Core`, `src/WinManager.Contracts`, `src/WinManager.Host`, `src/WinManager.Windows`, or `src/WinManager.Client`.
Core test classes are under `tests/WinManager.Core.Tests`. IPC test classes are under `tests/WinManager.Ipc.Tests`.
Desktop-test safety instructions are in [Windows test instructions](../tests/WinManager.Windows.Tests/README.md).
The [architecture spec](../../architecture.md) defines the current module and validation boundaries.
