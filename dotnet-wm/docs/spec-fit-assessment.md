# Spec fit assessment

## Scope and authority

This report compares the current implementation with [product requirements](../../goal-and-requirements.md).
It records evidence and verification gaps. It does not replace the spec graph or declare incomplete features implemented.
The inspected runtime baseline is commit `2a1460b`. No runtime changes form part of this assessment.

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
| Useful floating behavior and semantic exclusions | Implemented / safe-tested | `ManagerEngine.ApplyModes`, `WorkspaceSet.Visibility`, and `WindowInfo.IsIgnored`. `WindowSemanticsTests.IgnoredWindowsRemainObservableAndUntouchedUntilExplicitlyTiled` and `ClassificationChangesReflowAutomaticallyAndManualModesSurviveRescans`. | Representative real applications, popup/owner lifecycle, and interaction with each additional mode. |
| Coherent keyboard and mouse controls | Partial / different | Commands and shortcuts converge on the engine. Container selection and split changes have Core/IPC coverage. `setSplit` changes existing containers, not a general insertion preference. | Separate insertion direction from container reorientation. Verify the same arrangement through both input paths and make chosen intent observable. |

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
2. **Focused-split behavior:** settle the product scenarios before implementation. Cover local insertion, direction, resize, closure, exclusions, focus races, and unchanged unrelated workspaces.
3. **Direct-manipulation drops:** define edge/center hit areas and native event inputs, then verify insertion, swaps, cancellation, and cross-monitor transfer.
4. **Continuity and configuration:** design persistence/window matching and live reload as separate changes. Reload includes native-resource failure rollback, not just JSON validation.
5. **Additional modes and experiments:** define mode-specific behavior after the split workflow provides a stable control and validation foundation.

Every behavior change has three gates: automated contract tests, isolated native evidence, and owner confirmation of the actual workflow.
Keep durable specs aligned with each accepted implementation. A green safe suite cannot replace native validation or owner UX approval.

## Evidence navigation

Source files above are under `src/WinManager.Core`, `src/WinManager.Contracts`, `src/WinManager.Host`, `src/WinManager.Windows`, or `src/WinManager.Client`.
Core test classes are under `tests/WinManager.Core.Tests`. IPC test classes are under `tests/WinManager.Ipc.Tests`.
Desktop-test safety instructions are in [Windows test instructions](../tests/WinManager.Windows.Tests/README.md).
The [architecture spec](../../architecture.md) defines the current module and validation boundaries.
