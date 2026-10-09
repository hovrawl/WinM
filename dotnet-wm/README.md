# WinManager

Windows-only, headless .NET 10 window manager under construction. The adjacent
GlazeWM checkout is a behavioral reference and is not modified or linked.

## Current implementation

The first delivery stage observes the desktop without moving or hiding windows.
`Contracts` contains managed desktop records; `Core` reconciles notifications and
periodic rescans; `Windows` isolates generated CsWin32 interop on a dedicated
message thread; `Host` composes these with dependency injection as a `WinExe`.
Tiling, workspaces/recovery, public IPC/client/CLI, and user configuration belong
to subsequent delivery stages and are not available yet.

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

## Reference provenance

The GlazeWM reference is licensed under GPLv3. This stage is an original C#
implementation using Windows API documentation and generated CsWin32 bindings;
no Rust source has been copied or adapted.