# Windows platform tests

Desktop-independent tests cover identity replacement and retention, eligibility,
notification coalescing/overflow, and cancellation before native startup.

```powershell
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'Category!=IsolatedInteractive'
```

## Isolated interactive validation

Run only in a disposable Windows VM's logged-in interactive desktop with no
other window manager. Windows 10 build 15063 or newer is required. A Session-0
agent or ordinary developer desktop is not an appropriate test environment.

The observation tests spawn their own test-window process and cover discovery/filtering,
title/geometry/visibility/minimize/fullscreen changes, focus/display notifications,
and repeated startup/cancellation/disposal. Tests close their disposable windows.
The focus test requires a session where Windows allows the fixture to take focus.

Window-action tests cover visible-frame placement without activation or z-order
changes, exact requested sizes below application minimum hints, best-effort focus with verified success
or structured denial, stale identities, invalid dimensions, bounded minimum-size
queries, and cancellation/disposal of queued requests.

`WindowsShortcutInteractiveTests` registers real global hotkeys on each observer's
native thread. It covers conflicts with partial rollback, repeated replacement,
disposal/unregistration, invalid and duplicate bindings, correct command IDs,
stale-message rejection, queue saturation, cancellation, and graceful helper exit.
Delivery tests post `WM_HOTKEY` only to the observer's own hidden window; they never
inject keyboard input or invoke operating-system shortcuts.

After `StartAsync`, call `RegisterShortcutsAsync(IReadOnlyList<ShortcutBinding>,
CancellationToken)` and consume `ShortcutCommands(CancellationToken)` with one
asynchronous reader. Host should await manager readiness before registering and
forward each command to the same `ManagerEngine.ExecuteAsync` path used by IPC.
An empty binding list unregisters all shortcuts. Up to 256 bindings accept modifier
bits `0x1`, `0x2`, `0x4`, `0x8`, and optional `0x4000` (`MOD_NOREPEAT`); virtual keys
must be `1..254`, excluding reserved `F12`. Duplicate combinations ignore
`MOD_NOREPEAT`. Invalid input leaves the previous registrations intact; valid input
replaces them, and native registration failure rolls back the entire new set.
Failures include `invalid_shortcut`, `duplicate_shortcut`, `shortcut_conflict`,
`access_denied`, `native_error`, and `shortcut_capacity`, with no window GUID.
Native IDs are not reused during the observer session. Callback signals use a
64-entry nonblocking queue that drops new signals when saturated; signals from a
replaced set are discarded by the reader. Once queued, registration cancellation
awaits native completion/rollback rather than leaving unobserved registrations.
Disposal frees registrations and completes the command stream. Successful recovery
completion already waits for helper process exit (bounded to five seconds), and
both `RestoreAsync` and disposal now have explicit native lifecycle coverage.

`WindowsManagerIntegrationTests` runs the full `ManagerEngine` with a real
`WindowsDesktopObserver` and two independent disposable fixture processes. A
test-only adapter exposes only exact fixture titles/classes, verifies their PIDs,
and refuses actions for any other GUID. Actual observed native rectangles must
converge to `SplitTree.Layout`: horizontal partition, vertical partition, resize,
and swap, including physical-pixel work-area edges and DPI-scaled gaps. A separate
test sets both windows' real native minimum hints larger than the work area and
requires every window to reach its requested undersized placement with no
`layout_overflow` warnings. No unrelated window is managed, and no fake physical
geometry is used.

To see the layouts before the disposable windows close, use the opt-in hold below.
The hold defaults to zero and is capped at 15 seconds total, divided equally
between horizontal, vertical, resized, and swapped layouts. Run from `dotnet-wm`
only in the isolated interactive session:

```powershell
$env:WINMANAGER_ISOLATED_INTERACTIVE = '1'
$env:WINMANAGER_TEST_LAYOUT_HOLD_SECONDS = '12'
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'FullyQualifiedName~WindowsManagerIntegrationTests' --logger 'console;verbosity=detailed'
Remove-Item Env:\WINMANAGER_TEST_LAYOUT_HOLD_SECONDS
```

`WindowsDesktopObserver` implements `IWindowPlatform`. After startup and capture,
call `ApplyAsync(LayoutPlan, CancellationToken)` using captured window GUIDs.
Positioning is asynchronous to avoid blocking on a hung target: an empty failure
list means native requests were accepted, not that a target honored the geometry.
Capture again to obtain actual geometry. Bounds and minimum sizes describe the
visible frame in physical pixels; invisible resize borders are accounted for.
Layout bounds are authoritative: native positioning requests the exact supplied
visible size without clamping to minimum hints. A target application may still
choose not to honor the request.
Minimum tracking queries use a 50 ms timeout with DPI-specific system fallback.
Visibility actions register durable, acknowledged recovery protection before hiding.
The helper is also started before initial capture to recover abandoned journals;
an unavailable helper does not prevent observation or positioning, but disables hiding.
Manager-hidden windows retain their GUID and appear with `Visible=false` and
`ManagerHidden=true`. Application-hidden windows are excluded and application-minimized
windows are not hidden by the manager. `RestoreAsync(CancellationToken)` restores
original show state and only completes the helper after successful native restoration;
disposal attempts the same restoration without cancellation.

Recovery tests cover helper startup/acknowledgment refusal, graceful restoration,
helper death while the host lives, forced test-host exit, original maximized state,
identity mismatches, and stale journals without touching live-host records.
The test build copies the helper and dependencies into both its `Recovery` output
folder and the output root, so explicit and default helper paths both work.
For Host integration, build `src\WinManager.Recovery\WinManager.Recovery.csproj` and
copy `WinManager.Recovery.exe`, `.dll`, `.deps.json`, `.runtimeconfig.json`, and its
`WinManager.Windows.dll`, `WinManager.Core.dll`, and `WinManager.Contracts.dll`
dependencies into one directory. By default the executable is a sibling of the Host;
alternatively pass `recoveryExecutablePath` to `WindowsDesktopObserver`.
The helper requires the .NET 10 runtime. Journals reside in the user's private
`%LOCALAPPDATA%\WinManager\Recovery` directory. Uncertain or failed restoration
retains records for a later helper launch; inaccessible identities fail closed.

Cancellation is cooperative between actions; already-issued actions are not rolled
back. Focus is best effort, does not bypass foreground restrictions, and verifies
the foreground window immediately after the request.

From `dotnet-wm` in that isolated session:

```powershell
$env:WINMANAGER_ISOLATED_INTERACTIVE = '1'
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'Category=IsolatedInteractive'
Remove-Item Env:\WINMANAGER_ISOLATED_INTERACTIVE
```

The opt-in variable is a safety assertion, not a way to provision isolation.
Without it the desktop tests report skipped; those skips are not desktop
validation. Physical mixed-DPI and real hotplug checks additionally require
appropriate monitor hardware or a configured multi-display VM.