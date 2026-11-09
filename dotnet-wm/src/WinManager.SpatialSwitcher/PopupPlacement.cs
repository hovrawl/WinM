using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using WinManager.Contracts;
using ContractPixelRect = WinManager.Contracts.PixelRect;

namespace WinManager.SpatialSwitcher;

public readonly record struct PopupMonitor(Guid? MonitorId, ContractPixelRect WorkArea, uint Dpi);

public static class PopupPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;

    public static PopupMonitor? CaptureForegroundMonitor(IReadOnlyList<MonitorInfo> monitors)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var foreground = GetForegroundWindow();
        if (foreground == 0) return null;
        var handle = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (handle == 0) return null;

        var native = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
        if (!GetMonitorInfo(handle, ref native)) return null;

        var workArea = new ContractPixelRect(native.WorkArea.Left, native.WorkArea.Top,
            native.WorkArea.Right - native.WorkArea.Left, native.WorkArea.Bottom - native.WorkArea.Top);
        var matchingMonitor = monitors.FirstOrDefault(monitor =>
                                  string.Equals(monitor.DeviceName, native.DeviceName,
                                      StringComparison.OrdinalIgnoreCase)) ??
                              monitors.FirstOrDefault(monitor => monitor.Bounds.X == native.Monitor.Left &&
                                  monitor.Bounds.Y == native.Monitor.Top &&
                                  monitor.Bounds.Width == native.Monitor.Right - native.Monitor.Left &&
                                  monitor.Bounds.Height == native.Monitor.Bottom - native.Monitor.Top);
        var dpi = GetEffectiveDpi(handle);
        return new PopupMonitor(matchingMonitor?.Id, workArea, dpi);
    }

    public static PopupMonitor? FromSnapshot(IReadOnlyList<MonitorInfo> monitors, Guid? preferredMonitorId = null)
    {
        var monitor = preferredMonitorId is { } id
            ? monitors.FirstOrDefault(candidate => candidate.Id == id)
            : null;
        monitor ??= monitors.OrderBy(candidate => candidate.Bounds.X == 0 && candidate.Bounds.Y == 0 ? 0 : 1)
            .ThenBy(candidate => candidate.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id).FirstOrDefault();
        return monitor is null
            ? null
            : new PopupMonitor(monitor.Id, monitor.WorkArea, monitor.Dpi == 0 ? 96u : monitor.Dpi);
    }

    public static PixelPoint Center(ContractPixelRect workArea, double windowWidth, double windowHeight, uint dpi)
    {
        var scale = (dpi == 0 ? 96u : dpi) / 96d;
        var width = Math.Min(Math.Max(0, workArea.Width), Math.Max(0, (int)Math.Ceiling(windowWidth * scale)));
        var height = Math.Min(Math.Max(0, workArea.Height), Math.Max(0, (int)Math.Ceiling(windowHeight * scale)));
        return new PixelPoint(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2);
    }

    public static void Place(Window window, PopupMonitor? monitor)
    {
        if (monitor is not { } target) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = Center(target.WorkArea, window.Width, window.Height, target.Dpi);
    }

    private static uint GetEffectiveDpi(nint monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, EffectiveDpi, out var x, out _) == 0 && x > 0 ? x : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
