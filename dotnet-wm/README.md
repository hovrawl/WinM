# HomeDesk

Windows-only, headless .NET 10 tiling window manager, `HomeDeskSpace`, with the
separate Avalonia management and switching app `HomeDeskManager` for its **one
shared desk**. The adjacent GlazeWM checkout is a behavioral reference and is
not modified or linked.

## Current implementation

The system now keeps workspaces on one desk and monitor views separate from
workspace identity. See [the one-desk goal](docs/project-goal.md) and its
[migration plan](docs/desk-model-plan.md) for implemented behavior and remaining work.

- Native desktop discovery, stable window identities, serialized commands,
  horizontal/vertical split layouts and layered page stacks, DPI-scaled gaps,
  minimum-size-aware overflow staging with recovery-protected minimization,
  directional focus/movement, split resize, floating mode, and exclusions.
- Semantic fullscreen/popup/tool/owned/no-activate exclusions by default, with
  per-window `setMode`/`toggleMode` overrides through shortcuts, CLI, and SDK.
- Explicitly returning a window to floating restores its last observed pre-tiling
  floating bounds once, clamped to its workspace view's work area. This does not
  continually constrain user movement beyond the existing floating-window clamping.
- Free window dragging while managed: drop over a tile to swap positions, or
  onto another monitor to transfer to its active workspace and reflow the layout.
- Live tiled-window resizing: neighboring tiles reflow as split boundaries move,
  and the new proportions persist on release. Default columns share width equally;
  windows that cannot fit remain managed as minimized layout overflow.
- Per-workspace `split` or `layered` layout selection. Layered pages overlap at
  useful sizes and can be navigated with tiling focus-cycle commands; pages that
  individually exceed the work area remain visible as layout overflow.
- Desk-owned workspaces with exclusive, transferable monitor views, deterministic
  source fallback, monitor hotplug handling, safe visibility, and an acknowledged
  crash-recovery helper. `WorkspaceViews` exposes current monitor assignments.
- Manager-owned logical workspace positions are independent of monitor pixels and
  DPI, exposed in snapshots, seeded by configuration, and editable with
  `setWorkspacePosition` without moving native windows.
- Same-user/session named-pipe queries, commands, atomic snapshot/event
  subscriptions, bounded clients, a .NET SDK, and a headless CLI.
- Validated JSON configuration, optional registered shortcuts, pause/redraw/exit,
  rotating logs, single-instance protection, and explicit logon registration.
- A code-only Avalonia debug console shows workspaces, monitor views, windows,
  overflow, exclusions and recent events, with explicit controls through the SDK.
  `HomeDeskManager` maps workspace groups to HomeDeskSpace-owned desk
  coordinates. See the [debug console guide](src/HomeDesk.DebugUi/README.md)
  and [HomeDeskManager guide](src/HomeDeskManager/README.md).

`Core` has no generated native dependencies. `Windows` isolates CsWin32 on its
native message thread. `HomeDeskSpace` composes them through dependency injection as a
normal-privilege `WinExe` with a native Win32 tray icon and graceful Quit;
`Client` depends only on public contracts. There is no browser bridge or
arbitrary shell execution.
The spatial switcher is implemented; a full draggable/zoomable desk overview,
columns/rows/Fibonacci workspace rules, and persistent runtime desk positions or
view assignments remain future work.

## Existing feature progress

Container selection adds active-tree navigation, observable logical selection,
and selected-container split orientation through CLI, SDK, and shortcuts.
Filtered focus cycling adds next/previous wrapping within active workspaces,
with optional tiling/floating filters and observed focus events.
See [the feature record](docs/iteration-2.md) for completed work. It is a history,
not a functional-parity roadmap.

## Build and safe tests

Requires Windows 10 build 15063 or newer and the pinned .NET SDK in `global.json`.

```powershell
dotnet build HomeDesk.sln
dotnet test HomeDesk.sln --filter 'Category!=IsolatedInteractive'
```

Native desktop integration tests must run only in a disposable, isolated
interactive Windows session, never Session 0 or a normal working desktop.
Their opt-in instructions are in `tests\HomeDesk.Windows.Tests\README.md`.
Do not start the host on a desktop managed by another window manager.

Run either visual app in safe sample mode without connecting to the manager:

```powershell
dotnet run --project .\src\HomeDesk.DebugUi\HomeDesk.DebugUi.csproj -- --demo
dotnet run --project .\src\HomeDeskManager\HomeDeskManager.csproj -- --demo
```

Dependencies are version-pinned and NuGet lock files are committed with source.
Use `dotnet restore --locked-mode` to enforce the resolved dependency graph.

## Publish and run

```powershell
.\scripts\Publish.ps1 -Mode Release
.\scripts\Publish.ps1 -Mode Release -SelfContained
.\scripts\Publish.ps1 -Mode NativeAot
& '.\artifacts\win-x64\framework-dependent\host\HomeDeskSpace.exe'
& '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'snapshot'
& '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'watch' '--reconnect'
& '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'status'
'{"kind":"pause","paused":true}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"activateWorkspace","workspaceName":"2","monitorId":"<monitor-guid>"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
'{"kind":"exit"}' | & '.\artifacts\win-x64\framework-dependent\cli\HomeDesk.Cli.exe' 'command' '-'
```

The script publishes all apps by default. Release HomeDeskSpace and Recovery
outputs share a folder and must remain together; NativeAOT HomeDeskSpace is one
executable that starts its separate recovery worker from its own image.
Framework-dependent output requires the .NET 10 x64 runtime; `-SelfContained`
includes the runtime. HomeDeskSpace and both Avalonia apps support win-x64
NativeAOT; Avalonia statically links its rendering dependencies. To publish only
HomeDeskSpace, use `.\scripts\Publish.ps1 -Mode NativeAot -App Host`.
See the [publishing guide](docs/publishing.md) for
prerequisites, smoke checks and output details. Startup registration is opt-in;
see `src\HomeDesk.Cli\README.md`.

Default configuration: `%LOCALAPPDATA%\HomeDesk\config.json`, created on first
launch. Logs: `%LOCALAPPDATA%\HomeDesk\Logs`. Recovery journals:
`%LOCALAPPDATA%\HomeDesk\Recovery`. Host supports explicit `--config`,
`--data-dir`, `--pipe`, and `--recovery-executable` overrides. Only one host can
run for the same user/session, even with pipe overrides. Exit code `5` means
another instance owns the session; `2` indicates startup/lifetime failure and
`6` indicates incomplete shutdown restoration.

For the full command reference and third-party CLI, .NET SDK, and named-pipe
integration examples, see [External commands API](docs/external-api.md).
For an updating workspace/window/event display with overflow titles when needed, use `status`; see the
[live status guide](src/HomeDesk.Cli/README.md#live-status).
See also `docs\configuration.md`, `docs\protocol.md`, `docs\validation.md`,
`docs\iteration-2.md`, [the desk goal](docs/project-goal.md), and
[the desk migration plan](docs/desk-model-plan.md).
Commands commit logical intent; native operations are best-effort. Snapshots and
events report observed physical geometry, not assumed successful placement.
Managing elevated windows is best-effort and can fail without administrator rights.

## Reference provenance

The GlazeWM reference is licensed under GPLv3. This stage is an original C#
implementation using Windows API documentation and generated CsWin32 bindings;
no Rust source has been copied or adapted.