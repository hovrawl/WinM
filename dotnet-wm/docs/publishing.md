# Release publishing

Publishing targets Windows x64 by default and uses the pinned .NET 10 SDK and
locked NuGet dependency graph. Run the commands from the repository root in
PowerShell:

```powershell
.\scripts\Publish.ps1 -Mode Release
.\scripts\Publish.ps1 -Mode Release -SelfContained
.\scripts\Publish.ps1 -Mode NativeAot
```

The first command produces framework-dependent Host, Recovery, CLI, Debug UI,
and Spatial Switcher outputs. The second publishes the same apps self-contained.
The third publishes the two Avalonia clients as statically linked, single-file
NativeAOT executables. Select an individual app with `-App Host`, `-App Cli`,
`-App DebugUi`, or `-App SpatialSwitcher`; `NativeAot` accepts only `DebugUi`
and `SpatialSwitcher` (`All` means both). Static Avalonia publishing currently
supports only `win-x64`; the other modes retain their existing RID option.
By default, outputs are written under `artifacts\`. Change the destination root
with `-OutputRoot`.

Outputs are separated by RID and deployment mode, for example:

- `artifacts\win-x64\framework-dependent\host`
- `artifacts\win-x64\self-contained\host`
- `artifacts\win-x64\native-aot\debug-ui`

Keep the complete output directory together. Host publishing runs Recovery
publishing into the same output directory with the same RID and deployment mode;
Recovery is a separate process and must be available beside Host. Framework-
dependent output requires the .NET 10 Windows Desktop runtime. Self-contained
output includes the .NET runtime.

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
& '.\artifacts\win-x64\native-aot\spatial-switcher\WinManager.SpatialSwitcher.exe' '--smoke'
```

Smoke mode displays sample content for two seconds and exits; it does not
exercise a live manager connection, global hotkeys, or real desktop management.

## Host NativeAOT status

Host is not currently an advertised NativeAOT target. Its configuration,
protocol, Core snapshot signatures, CLI output, and Recovery command/journal
serialization now use generated `System.Text.Json` metadata rather than relying
on reflection for those paths. JSON was not the only obstacle: the Host tray
currently uses Windows Forms. A future Host AOT effort should first replace
that tray dependency with a native `Shell_NotifyIcon` implementation while
preserving its STA message loop and graceful shutdown, then attempt a Host plus
Recovery NativeAOT publish and address any remaining trimming/AOT diagnostics.
Until that work and runtime behavior are validated, publish Host using the
framework-dependent or self-contained Release commands above.
