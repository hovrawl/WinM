namespace WinManager.Contracts;

public sealed record PixelRect(int X, int Y, int Width, int Height);
public enum WindowMode { Tiling, Floating }
public sealed record MonitorInfo(Guid Id, string DeviceName, PixelRect Bounds, PixelRect WorkArea, uint Dpi);
public sealed record WindowInfo(Guid Id, Guid MonitorId, string Title, string ProcessName,
    string ClassName, PixelRect Bounds, bool Visible, bool Minimized, bool Fullscreen,
    WindowMode Mode, int MinimumWidth = 1, int MinimumHeight = 1, bool ManagerHidden = false);
public sealed record DesktopSnapshot(IReadOnlyList<MonitorInfo> Monitors,
    IReadOnlyList<WindowInfo> Windows, Guid? FocusedWindowId);