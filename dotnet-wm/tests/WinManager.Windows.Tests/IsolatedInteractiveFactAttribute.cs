using Xunit;

namespace WinManager.Windows.Tests;

public sealed class IsolatedInteractiveFactAttribute : FactAttribute
{
    public IsolatedInteractiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINMANAGER_ISOLATED_INTERACTIVE") != "1" ||
            !Environment.UserInteractive || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
            Skip = "Requires an isolated interactive Windows desktop and WINMANAGER_ISOLATED_INTERACTIVE=1. Never opt in on a normal desktop.";
    }
}