# Release publishing

Publishing targets Windows x64 by default and uses the pinned .NET 10 SDK and
locked NuGet dependency graph. Run the commands from the repository root in
PowerShell:

```powershell
.\scripts\Publish.ps1 -Mode Release
.\scripts\Publish.ps1 -Mode Release -SelfContained
.\scripts\Publish.ps1 -Mode NativeAot
```

The first command produces framework-dependent `HomeDeskSpace`, Recovery, CLI,
Debug UI, and `HomeDeskManager` outputs. The second publishes the same apps self-contained.
The third publishes Host and both Avalonia clients as single-file NativeAOT
executables. Host uses a Win32 tray and launches its recovery worker from its
own executable; the Avalonia clients statically link their rendering libraries.
Select an individual app with `-App Host`, `-App Cli`, `-App DebugUi`, or
`-App SpatialSwitcher`; `NativeAot` does not support `Cli` (`All` means Host and
both clients). NativeAOT publishing currently supports only `win-x64`; the other
modes retain their existing RID option.
By default, outputs are written under `artifacts\`. Change the destination root
with `-OutputRoot`.
Publish-specific RID/toolchain lock files live under each project's `obj` folder,
leaving the committed development lock files unchanged.

Outputs are separated by RID and deployment mode, for example:

- `artifacts\win-x64\framework-dependent\host`
- `artifacts\win-x64\self-contained\host`
- `artifacts\win-x64\native-aot\host`
- `artifacts\win-x64\native-aot\debug-ui`

Keep the complete output directory together. Framework-dependent and
self-contained HomeDeskSpace publishing include the separate Recovery executable beside
the host. The NativeAOT HomeDeskSpace instead launches its own executable in a recovery-only
mode, before normal manager startup; the recovery process still runs separately
from the manager. Framework-dependent output requires the .NET 10 x64 runtime.
Self-contained and NativeAOT outputs include the .NET runtime.

## Avalonia single-file NativeAOT

The script enables static linking only for this mode, leaving ordinary Debug and
Release builds unchanged. It uses pinned `2ndLAB.ANGLE.Static` and
`2ndLAB.SkiaSharp.Static` packages plus the Windows x64 linker settings in
`scripts\AvaloniaSingleFile.props`. These are private build-only dependencies,
so their archives do not become runtime assets in ordinary builds. The target
removes only the corresponding dynamically loaded native DLLs, linker archives,
and PDBs from single-file publish items. The script verifies
that each output directory contains exactly the expected executable. It never
deletes stale files, so move unexpected files out of the selected output
directory before publishing again.

NativeAOT requires the .NET 10 SDK, Windows C++ build tools, and Windows SDK
needed by the native compiler. The script pins self-contained/AOT output to the
.NET 10.0.14 runtime pack; keep that patch aligned with the latest security
release. Avalonia static single-file publishing rejects RIDs other than
`win-x64` until compatible static libraries are configured and validated.

The configuration follows the [Avalonia NativeAOT SingleFile
reference](https://github.com/peaceshi/Avalonia-NativeAOT-SingleFile), using its
static Skia/ANGLE integration pattern with Avalonia 12. This is actual native
static linking, not the `PublishSingleFile` managed bundle option.

Run the published executables in isolated visual smoke mode to open their demo
UI, avoid the manager and global keyboard hook, then exit automatically:

```powershell
& '.\artifacts\win-x64\native-aot\debug-ui\WinManager.DebugUi.exe' '--smoke'
& '.\artifacts\win-x64\native-aot\spatial-switcher\HomeDeskManager.exe' '--smoke'
```

Smoke mode displays sample content for two seconds and exits; it does not
exercise a live manager connection, global hotkeys, or real desktop management.

## Host NativeAOT

```powershell
.\scripts\Publish.ps1 -Mode NativeAot -App Host
```

HomeDeskSpace uses a native hidden Win32 window and `Shell_NotifyIcon` tray icon through
the pinned `Microsoft.Windows.CsWin32` generated bindings, with a
Quit menu item that requests graceful host shutdown. Its tray icon returns after
Explorer restarts, and hides while a connected HomeDeskManager session is active.
The manager's disconnect releases the lease and restores the host icon. A NativeAOT HomeDeskSpace is a
single executable: it starts a separate recovery process by invoking itself
with the internal `--recovery` mode. That mode enters the recovery protocol
before normal configuration, singleton, tray, or desktop-observation startup.

Use `--tray-smoke` to briefly create and remove the native tray icon without
starting the manager or observing desktop windows:

```powershell
& '.\artifacts\win-x64\native-aot\host\HomeDeskSpace.exe' '--tray-smoke'
& '.\artifacts\win-x64\native-aot\host\HomeDeskSpace.exe' '--recovery-smoke'
```

The recovery smoke check self-launches the worker, verifies the normal authenticated
pipe READY/ACK and graceful completion, and removes its empty temporary journal
directory beside the executable. It never scans the user's normal recovery
journals or starts desktop observation. Run it from a writable publish directory.
Neither smoke check exercises window restoration after a real manager crash or
the tray's interactive Quit menu; those still need target-desktop validation.
