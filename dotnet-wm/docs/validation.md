# MVP validation

Validated on 2026-10-09 in the user-confirmed isolated interactive Windows
session, with `WINMANAGER_ISOLATED_INTERACTIVE=1`.

```powershell
$env:WINMANAGER_ISOLATED_INTERACTIVE = '1'
dotnet test WinManager.sln --verbosity minimal
Remove-Item Env:\WINMANAGER_ISOLATED_INTERACTIVE
dotnet restore WinManager.sln --locked-mode
```

Full suite: **234 passed, zero failed, zero skipped**:
- Core: 62 deterministic layout, observation, command, workspace, failure,
  feedback-loop, admission, subscription, and monitor-transfer tests.
- IPC: 101 framing, security, ordering, SDK/CLI, configuration, logging,
  single-instance lease, and disposable startup-registration tests.
- Windows: 71 native observation/action, pipe ACL, hotkey, recovery, and
  full-manager/production-host integration tests.

The seven production-host tests launch the real headless host and helper against
test-owned windows. They cover native split geometry, snapshot/subscription/
commands/events, graceful restoration, forced-host-exit restoration, identity
mismatch and unrelated-window protection, helper-start failure, second-instance
rejection, and invalid configuration before window management.

Both framework-dependent Windows x64 host and CLI publish successfully. The
deployment check verifies the recovery executable, DLL, runtime configuration,
dependency manifest, and matching Windows/Core/Contracts assemblies accompany
the host. Keep the complete publish directories, not individual executables.

## Seeing the real test layout

Earlier native tests positioned individual windows; core split tests used fake
platforms. The new full-manager native test creates **two separate disposable
window processes** and checks actual rectangles against horizontal, vertical,
resized, and swapped split plans. No unrelated window is admitted by its adapter.

In an isolated interactive session, retain the layouts briefly for inspection:

```powershell
$env:WINMANAGER_ISOLATED_INTERACTIVE = '1'
$env:WINMANAGER_TEST_LAYOUT_HOLD_SECONDS = '12'
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'FullyQualifiedName~EngineTilesTwoNativeProcessesHorizontallyThenVerticallyResizesAndSwaps' --logger 'console;verbosity=detailed'
Remove-Item Env:\WINMANAGER_TEST_LAYOUT_HOLD_SECONDS
Remove-Item Env:\WINMANAGER_ISOLATED_INTERACTIVE
```

The optional hold defaults to zero, caps at 15 seconds total, and is split among
four arrangements. Windows close after verification; a fast run is deliberately
not a persistent visualization. The opt-in variable does not create isolation.

## Remaining deployment validation

- Real monitor hotplug/mixed-DPI hardware and physical keyboard activation were
  not exercised. Topology/DPI behavior is simulated in Core, and native display
  notifications/hotkey registration and callback delivery are covered.
- Startup registration uses disposable real HKCU values; actual sign-out/logon
  execution was not performed.
- Cross-user, cross-session, and remote-machine negative connections need
  external credentials/sessions. Native tests verify the protected current-user
  SID-only DACL; public pipe creation uses kernel remote-client rejection, with
  process/session and impersonated SID verification before command dispatch.

These are deployment-validation limitations, not a claim of hardware or all-user
coverage. Iteration-two functionality remains separately deferred.