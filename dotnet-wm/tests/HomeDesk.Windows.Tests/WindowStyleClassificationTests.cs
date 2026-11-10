using System.Runtime.Versioning;
using Xunit;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowStyleClassificationTests
{
    [Theory]
    [InlineData(0u, 0u, false, false, false, false, false)]
    [InlineData(0x80000000u, 0u, false, true, false, false, false)]
    [InlineData(0u, 0x80u, false, false, true, false, false)]
    [InlineData(0u, 0u, true, false, false, true, false)]
    [InlineData(0u, 0x08000000u, false, false, false, false, true)]
    [InlineData(0x80000000u, 0x08000080u, true, true, true, true, true)]
    [InlineData(0x10CF0000u, 0x00040000u, false, false, false, false, false)]
    [InlineData(0x40000000u, 0u, false, false, false, false, false)]
    [InlineData(0x08000080u, 0x80000000u, false, false, false, false, false)]
    public void ClassifiesIndependentNativeBitsAndOwnerWithoutNativeCalls(uint style, uint extendedStyle,
        bool hasOwner, bool isPopup, bool isToolWindow, bool isOwned, bool isNoActivate)
    {
        Assert.Equal((isPopup, isToolWindow, isOwned, isNoActivate),
            WindowsDesktopObserver.ClassifyWindowStyles(style, extendedStyle, hasOwner));
    }
}