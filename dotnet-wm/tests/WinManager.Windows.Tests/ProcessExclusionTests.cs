using System.Runtime.Versioning;
using Xunit;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class ProcessExclusionTests
{
    [Theory]
    [InlineData("WinManager.DebugUi")]
    [InlineData("WINMANAGER.DEBUGUI.EXE")]
    [InlineData(@"C:\Apps\WinManager.DebugUi.exe")]
    [InlineData("WinManager.SpatialSwitcher")]
    [InlineData("WINMANAGER.SPATIALSWITCHER.EXE")]
    [InlineData(@"C:\Apps\WinManager.SpatialSwitcher.exe")]
    public async Task DebugCompanionIsExcludedEvenWithAnEmptyConfiguration(string processName)
    {
        await using var observer = new WindowsDesktopObserver(ignoredProcessNames: []);
        Assert.True(observer.IsProcessExcluded(processName));
        Assert.False(observer.IsProcessExcluded("WinManager.DebugUiExtra"));
        Assert.False(observer.IsProcessExcluded("WinManager.SpatialSwitcherExtra"));
        Assert.False(observer.IsProcessExcluded("other"));
    }

    [Fact]
    public async Task ConfiguredExclusionsRemainAdditionalToTheBuiltInCompanionExemption()
    {
        await using var observer = new WindowsDesktopObserver(ignoredProcessNames: ["Sample.exe"]);
        Assert.True(observer.IsProcessExcluded("sample"));
        Assert.True(observer.IsProcessExcluded("WinManager.DebugUi"));
        Assert.True(observer.IsProcessExcluded("WinManager.SpatialSwitcher"));
    }
}