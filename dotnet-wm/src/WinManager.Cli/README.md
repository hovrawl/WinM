# WinManager CLI

`net10.0-windows`; references `WinManager.Client`. Explicit per-user startup registration;
no shell execution or engine/Host dependencies. Includes a live terminal status display.

See [External commands API](../../docs/external-api.md) for all manager commands,
their arguments and behavior, and third-party integration examples.

From `C:\Dev\NahFarn\WinM\dotnet-wm`, build only the two new production projects:

```powershell
dotnet 'build' '.\src\WinManager.Client\WinManager.Client.csproj'
dotnet 'build' '.\src\WinManager.Cli\WinManager.Cli.csproj'
```

Exact invocations (Windows PowerShell 5.1 and PowerShell 7):

```powershell
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' '--help'
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'snapshot'
'{"kind":"pause","paused":true}' | dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'command' '-'
'{"kind":"redraw"}' | dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'command' '-'
'{"kind":"toggleMode","windowId":"<window-guid>"}' | dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'command' '-'
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'watch' '--event-types' 'pause.changed' '--reconnect'
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'status'
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'snapshot' '--pipe' 'WinManager.Test.Pipe'
'{"kind":"pause","paused":false}' | dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'command' '-' '--pipe' 'WinManager.Test.Pipe'
```

`command JSON` also accepts a single positional JSON argument when the invoking shell preserves
embedded quotes. Prefer the stdin form above across PowerShell versions: Windows PowerShell strips
embedded quotes in native positional arguments. JSON stdin is data only and is never executed.
Unknown JSON members and missing/invalid command kinds are rejected before connecting.
Replace `<window-guid>` with an ID from `snapshot`. `toggleMode` switches an
individual window between tiled and untiled; omit `windowId` to use the focused
observable window. Fullscreen, popup, and tool windows remain untouched by default,
but this command can explicitly opt them into tiling. Use `setMode` with `mode`
`tiling` or `floating` when you need a known state rather than a toggle.

## Interactive startup

Start the CLI without arguments to choose **Status observer**, **Send commands**, **Help**, or **Exit**:

```powershell
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll'
```

Enter the menu number and press Enter. Status and command entry prompt for a pipe name;
leave it blank to use the default Host pipe. Command entry accepts one JSON `ManagerCommand`
per line (for example, `{"kind":"pause","paused":true}`), prints each result, and lets you
send another command. A blank command line returns to the menu. Invalid JSON, rejected
commands, and connection failures report the existing JSON errors without ending the session.
Help returns to the menu; status returns if the subscription ends or fails. Ctrl+C exits the
CLI, including while observing status. Choose `0` to exit, or close stdin to exit cleanly.
The menu also works with redirected input/output. Explicit one-off invocations are unchanged.

## Live status

Run `status [--pipe NAME] [--reconnect]` while the Host is running. It subscribes to
all SDK events and replaces four lines in place, starting from the atomic initial snapshot:

```text
Current Workspace: Code
Current Window: Editor [editor] (window GUID)
Current Config direction: Not exposed by host
Latest event: focus.changed (sequence 7, revision 12; running)
```

The current workspace belongs to the focused managed window. With no managed focus,
all active workspaces are listed (one per monitor); absent windows/workspaces show `None`.
The host does not expose a global configuration/next-split direction in its snapshots;
existing tree orientations are not a substitute, so that field is explicitly unavailable.
The latest event includes sequence, state revision, and paused/running state.

Lines are clipped to the terminal width and cleared when values get shorter. Redirected
stdout (or a terminal too small for the buffer) appends plain four-line blocks instead;
use `watch` for machine-readable JSON. Ctrl+C stops the subscription. `--reconnect`
uses the SDK transport retry behavior; the last displayed state remains visible during
retry waits and is replaced by a fresh snapshot after reconnecting. Events are not replayed.

Startup is never installed automatically. To opt in, supply your deployed Host executable and
optionally an existing JSON configuration file (both paths must be absolute):

```powershell
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'install-startup' '--host' 'C:\Apps\WinManager\WinManager.Host.exe' '--config' 'C:\Apps\WinManager\config.json'
dotnet '.\src\WinManager.Cli\bin\Debug\net10.0-windows\WinManager.Cli.dll' 'uninstall-startup'
```

These commands do not connect to or start the Host. Install writes a quoted executable and optional
`--config` argument as a `REG_SZ` value named `WinManager` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. It overwrites only that value;
uninstall deletes only that value and succeeds if already absent. No administrator privileges,
services, scheduled tasks, or shell commands are used. Quotes, control characters, wildcards,
and shell metacharacters in paths are rejected; the Run command is limited to 260 characters.
Configuration contents are validated by the Host, not by registration. Host single-instance behavior
is owned by the Host. Registration results are JSON; validation errors exit 2 and registry errors exit 4.

The public `StartupRegistration.Write(hostPath, configPath, registrationName)` and
`Remove(registrationName)` API accepts only `WinManager` (default) or `WinManager.Test.<guid>`.
Tests must explicitly use a disposable name and remove it in `finally`; they never install or
uninstall the production value.

Snapshot and command results are compact JSON on stdout; watch prints newline-delimited
`EventEnvelope` JSON with full `ManagerSnapshot` payloads. Filters are forwarded to the server;
the initial snapshot is always delivered first. Errors are JSON on stderr. Reconnect resynchronizes
with a fresh snapshot and instance ID after transport failures; it does not replay lost transitions
or retry commands. Slow output consumers cause a fatal bounded-queue error instead of dropped events.
Ctrl+C cancels connection, requests, watch, and status, including retry waits.

| Exit code | Meaning |
| --- | --- |
| 0 | Success or help |
| 2 | Usage or command JSON error |
| 3 | Server/envelope rejection or unsuccessful command result |
| 4 | Connection, permissions, protocol, consumer lag, or startup registration failure |
| 130 | Ctrl+C cancellation |

The parent owns server integration and solution wiring. A server must be running for snapshot,
command, watch, and status; absent servers time out after five seconds (watch/status with reconnect retry).