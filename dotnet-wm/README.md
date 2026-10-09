# WinManager

Windows-only, headless .NET 10 tiling window manager. The adjacent
GlazeWM checkout is a behavioral reference and is not modified or linked.

## Tiling MVP

- Native desktop discovery, stable window identities, serialized commands,
  horizontal/vertical split layouts, DPI-scaled gaps, directional focus/movement,
  split resize, floating mode, and minimized/fullscreen exclusions.
- Free window dragging while managed: drop over a tile to swap positions, or
  onto another monitor to transfer to its active workspace and reflow the layout.
- Live tiled-window resizing: neighboring tiles reflow as split boundaries move,
  and the new proportions persist on release, respecting gaps and minimum sizes.
- Named per-monitor workspaces, monitor transfers/removal handling, safe
  workspace visibility, and an acknowledged crash-recovery helper.
- Same-user/session named-pipe queries, commands, atomic snapshot/event
  subscriptions, bounded clients, a .NET SDK, and a headless CLI.
- Validated JSON configuration, optional registered shortcuts, pause/redraw/exit,
  rotating logs, single-instance protection, and explicit logon registration.

`Core` has no generated native dependencies. `Windows` isolates CsWin32 on its
native message thread. `Host` composes them through dependency injection as a
normal-privilege `WinExe`; `Client` depends only on public contracts.
No tray, widget, service, browser bridge, or arbitrary shell execution is included.

## Build and safe tests

Requires Windows 10 build 15063 or newer and the pinned .NET SDK in `global.json`.

```powershell
dotnet build WinManager.sln
dotnet test WinManager.sln --filter 'Category!=IsolatedInteractive'
```

Native desktop integration tests must run only in a disposable, isolated
interactive Windows session, never Session 0 or a normal working desktop.
Their opt-in instructions are in `tests\WinManager.Windows.Tests\README.md`.
Do not start the host on a desktop managed by another window manager.

Dependencies are version-pinned and NuGet lock files are committed with source.
Use `dotnet restore --locked-mode` to enforce the resolved dependency graph.

## Publish and run

```powershell
dotnet publish src\WinManager.Host\WinManager.Host.csproj -c Release -r win-x64 --self-contained false -o publish\host
dotnet publish src\WinManager.Cli\WinManager.Cli.csproj -c Release -r win-x64 --self-contained false -o publish\cli
& '.\publish\host\WinManager.Host.exe'
& '.\publish\cli\WinManager.Cli.exe' 'snapshot'
& '.\publish\cli\WinManager.Cli.exe' 'watch' '--reconnect'
'{"kind":"pause","paused":true}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
'{"kind":"activateWorkspace","workspaceName":"2"}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
'{"kind":"exit"}' | & '.\publish\cli\WinManager.Cli.exe' 'command' '-'
```

The framework-dependent deployment requires the .NET 10 x64 runtime. Keep the
entire host publish directory together, including the recovery executable,
assembly, runtime configuration, and dependencies. Startup registration is
opt-in; see `src\WinManager.Cli\README.md`.

Default configuration: `%LOCALAPPDATA%\WinManager\config.json`, created on first
launch. Logs: `%LOCALAPPDATA%\WinManager\Logs`. Recovery journals:
`%LOCALAPPDATA%\WinManager\Recovery`. Host supports explicit `--config`,
`--data-dir`, `--pipe`, and `--recovery-executable` overrides. Only one host can
run for the same user/session, even with pipe overrides. Exit code `5` means
another instance owns the session; `2` indicates startup/lifetime failure and
`6` indicates incomplete shutdown restoration.

For the full command reference and third-party CLI, .NET SDK, and named-pipe
integration examples, see [External commands API](docs/external-api.md).
See also `docs\configuration.md`, `docs\protocol.md`, `docs\validation.md`, and `docs\iteration-2.md`.
Commands commit logical intent; native operations are best-effort. Snapshots and
events report observed physical geometry, not assumed successful placement.
Managing elevated windows is best-effort and can fail without administrator rights.

## Reference provenance

The GlazeWM reference is licensed under GPLv3. This stage is an original C#
implementation using Windows API documentation and generated CsWin32 bindings;
no Rust source has been copied or adapted.