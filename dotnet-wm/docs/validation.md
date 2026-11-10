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

## DWM carousel experiment

The preview query is bounded to 64 managed IDs per request. Deterministic IPC tests
cover strict argument validation, unknown IDs, unavailable previews, and response
identity checks. The DWM fake-adapter tests cover registration/update/unregistration
balance, stale identities, and failed source-size cleanup. Carousel geometry tests
cover aspect-preserving half-work-area bounds, non-overlapping cards, and source
cropping at the viewport edge.

The controlled native harness is
`PreviewTargetResolutionAndDwmBindingPreserveSourceWindowGeometry` in
`WindowsDesktopObserverInteractiveTests`. It creates a separate source window and
destination window, resolves the managed source identity, registers and updates a
DWM thumbnail, and confirms the source rectangle is unchanged across close/reopen.
Run it only in an explicitly isolated interactive desktop:

```powershell
$env:WINMANAGER_ISOLATED_INTERACTIVE = '1'
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'FullyQualifiedName~PreviewTargetResolutionAndDwmBindingPreserveSourceWindowGeometry'
Remove-Item Env:\WINMANAGER_ISOLATED_INTERACTIVE
```

The initial carousel implementation run passed 241 IPC tests and 86 Windows tests,
with 40 opt-in Windows tests skipped;
the DWM harness and all three touchpad/foreground probes were among the skipped
tests. Therefore actual DWM composition over the Avalonia destination, mixed-DPI
placement, and rendered clipping have not yet been visually confirmed here.
Geometry and identity tests are not evidence of physical touchpad report
availability or suppression of Windows shell gestures.

## Native touchpad input probe

The reusable input source registers the Precision Touchpad HID collection as raw
input, decodes usage pages and coordinate ranges from HID preparsed data, assembles
reports by device and scan time, and cancels recognition after malformed or dropped
input. Deterministic fixtures cover HID value decoding, exact `HIDP_CAPS` and
`HIDP_VALUE_CAPS` structure sizes, split contact frames, gesture count transitions,
diagonal movement, and queue-overflow reset behavior.

HID Contact Count determines how many contact slots are present in a report; it is
not the number of tips still touching the surface. The decoder preserves Tip Switch
off lift records in those counted slots and ignores unused trailing slots. Recognition
validates the reported slot count, counts only touching tips, retains swipe travel
through settling and staggered lifts, and publishes once when the last tip lifts; an
additional empty-contact packet is not required. Synthetic decoder-to-processor tests
cover this path and malformed releases, but do not establish the behavior of every
physical device.

The HID capability structs include the SDK reserved arrays (`HIDP_CAPS` is 64 bytes;
`HIDP_VALUE_CAPS` is 72 bytes). Native-byte fixtures pin the input-value-capability
count offset, value-range offsets, and value-capability array stride; the earlier
undersized declarations caused touchpad collections to be rejected during parsing.
The `HidP_GetValueCaps` element-count pointer also uses the native 16-bit width.
Unrelated HID collections are classified separately from unsupported touchpad
descriptors rather than producing misleading touchpad-decoding failures.

Validation for this correction passed 94 Windows tests, with 40 opt-in interactive
tests skipped, and built the solution with no warnings or errors. The ABI size
regression was verified failing against the earlier 32/64-byte layouts before the
fix was applied.

A read-only local descriptor probe found three Touch Pad collections and decoded
14, 16, and 17 input-value capabilities with the corrected ABI. Two collections
exposed the contact IDs, coordinates, and contact counts required by this decoder;
the other collection lacked those required fields. This establishes descriptor
availability, not physical contact-frame delivery or exclusive gesture ownership.

The [RawInput.Touchpad sample](https://github.com/emoacht/RawInput.Touchpad) confirms
the reserved-array layouts and 16-bit count parameter. Microsoft's
[Precision Touchpad APIs](https://learn.microsoft.com/en-us/windows/win32/input-precisiontouchpad/precision-touchpad-portal)
include `TouchpadGesturesController`, which overrides system handling only for a
foreground process; background controllers are ignored. These APIs are documented
with a pre-release notice and may offer a future foreground-carousel input path,
but do not establish gesture ownership for the background Host or global carousel
entry while another application is foreground.

Raw contact delivery is separate from shell-gesture suppression. The source reports
`gestureConflictBlocked` whenever a touchpad is present because observing raw HID
reports does not prove that Windows' built-in Precision Touchpad actions are
suppressed. `CanDispatchActions` remains false until both contact reports and
exclusive gesture ownership are verified. The Host's three-finger horizontal focus
policy separately accepts `CanRecognizeGestures`; it logs that Windows may also run
its configured action while suppression is unverified. Four-finger movement remains
gated on `CanDispatchActions`. The implementation does not change Windows settings.

Two isolated desktop probes are available in
`WindowsDesktopObserverInteractiveTests`: `RawTouchpadProbeReportsCapabilityWithoutClaimingShellGestureOwnership`
reports registration/device status, and
`RawTouchpadProbeCanObserveThreeFingerContactFrames` waits up to 30 seconds for a
physical three-finger contact frame. Run the latter only on an isolated interactive
desktop while making a three-finger contact gesture:

```powershell
$env:WINMANAGER_ISOLATED_INTERACTIVE = '1'
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'FullyQualifiedName~RawTouchpadProbeCanObserveThreeFingerContactFrames'
Remove-Item Env:\WINMANAGER_ISOLATED_INTERACTIVE
```

That probe can verify contact-report availability on the tested device, but it does
not verify suppression of Windows' own actions. In the current session, the
interactive probe was skipped, so neither physical contact delivery nor shell
coexistence is established. The Host can dispatch three-finger horizontal focus
when contacts are recognized, with a warning that Windows may also perform its
configured action; four-finger movement remains blocked until suppression is
verified.

## Host gesture policy

`touchpad.enabled` defaults to `true`, is compatible with configuration files that
omit the section, and can disable Host-native input. Deterministic tests verify the
three-finger horizontal inversion, four-finger `MoveSpatial` directions, vertical
gesture exclusion from Host commands, and capability routing: three-finger focus
uses observed contacts, while four-finger `MoveSpatial` requires verified exclusive
ownership. The Host warns that Windows may also perform its configured gesture when
suppression is unverified. The Host also requires the observer-resolved foreground
identity to match its current focused window before executing a command. Keyboard
shortcuts remain available regardless of native touchpad capability.

## SpatialSwitcher carousel entry

`Ctrl+Alt+Space` is a local SpatialSwitcher hook chord; tests verify it toggles
once per key press, consumes the matching release, and leaves existing Alt/Win
switcher chords unchanged. The carousel state tests verify that browsing only
changes the selected ID, acceptance creates a `Focus` command for that ID, and
cancel or external activation never creates an acceptance command. Escape and a
three-finger swipe down cancel to the captured original window; external
activation closes without restoring focus. Once contact reports are observed,
three-finger vertical gestures and two-finger scrolling route locally even though
exclusive shell ownership is unverified; Windows may also perform its configured
gesture, and the switcher displays that warning. Switcher policy inverts both signs
of the raw two-finger horizontal delta so carousel movement follows scroll direction.
Host three-finger horizontal focus also dispatches from observed contacts with an
explicit coexistence warning; Host four-finger movement remains gated on verified
shell suppression. Deterministic scroll tests
cover coalescing, bounds, viewport anchoring, selection preservation, and preview
source clipping; snapshot reconciliation closes on a changed workspace view or
monitor and rebinds when candidate identities change. These presentation paths
have not been exercised with live touchpad reports or visually confirmed DWM
composition in this session; physical touchpad routing and shell coexistence remain
unverified.