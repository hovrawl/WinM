# Windows observation tests

Desktop-independent tests cover identity replacement and retention, eligibility,
notification coalescing/overflow, and cancellation before native startup.

```powershell
dotnet test tests\WinManager.Windows.Tests\WinManager.Windows.Tests.csproj --filter 'Category!=IsolatedInteractive'
```

## Isolated interactive validation

Run only in a disposable Windows VM's logged-in interactive desktop with no
other window manager. Windows 10 build 15063 or newer is required. A Session-0
agent or ordinary developer desktop is not an appropriate test environment.

The four tests spawn their own test-window process and cover discovery/filtering,
title/geometry/visibility/minimize/fullscreen changes, focus/display notifications,
and repeated startup/cancellation/disposal. Tests close their disposable windows.
The focus test requires a session where Windows allows the fixture to take focus.

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