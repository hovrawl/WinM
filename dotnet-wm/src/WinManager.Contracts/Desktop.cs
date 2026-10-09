namespace WinManager.Contracts;

public sealed record PixelRect(int X, int Y, int Width, int Height);
public enum WindowMode { Tiling, Floating }
public sealed record MonitorInfo(Guid Id, string DeviceName, PixelRect Bounds, PixelRect WorkArea, uint Dpi);
public sealed record WindowInfo(Guid Id, Guid MonitorId, string Title, string ProcessName,
    string ClassName, PixelRect Bounds, bool Visible, bool Minimized, bool Fullscreen,
    WindowMode Mode, int MinimumWidth = 1, int MinimumHeight = 1, bool ManagerHidden = false,
    bool IsPopup = false, bool IsToolWindow = false, bool IsOwned = false, bool IsNoActivate = false,
    bool? TilingOverride = null)
{
    public bool IsIgnored => TilingOverride != true && (Fullscreen || IsPopup || IsToolWindow || IsOwned || IsNoActivate);
}
public sealed record DesktopSnapshot(IReadOnlyList<MonitorInfo> Monitors,
    IReadOnlyList<WindowInfo> Windows, Guid? FocusedWindowId);