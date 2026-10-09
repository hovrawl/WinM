using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.UI.WindowsAndMessaging;
using PInvoke = global::Windows.Win32.TestPInvoke;

namespace WinManager.Windows.Tests;

internal static class NativeInspection
{
    public static void GrantForeground(uint pid) => PInvoke.AllowSetForegroundWindow(pid);
    public static unsafe nint FindHandle(string title)
    {
        nint target = 0;
        WNDENUMPROC callback = (window, data) =>
        {
            Span<char> text = stackalloc char[4096];
            fixed (char* buffer = text)
            {
                var length = PInvoke.GetWindowText(window, buffer, text.Length);
                if (text[..Math.Max(0, length)].SequenceEqual(title)) target = (nint)window.Value;
            }
            return true;
        };
        if (!PInvoke.EnumWindows(callback, default)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        return target;
    }

    public static bool Visible(string title) => PInvoke.IsWindowVisible(new global::Windows.Win32.Foundation.HWND(FindHandle(title)));

    public static uint ProcessId(string title)
    {
        PInvoke.GetWindowThreadProcessId(new global::Windows.Win32.Foundation.HWND(FindHandle(title)), out var pid);
        return pid;
    }

    public static SHOW_WINDOW_CMD ShowCommand(string title)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.GetWindowPlacement(new global::Windows.Win32.Foundation.HWND(FindHandle(title)), ref placement))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return placement.showCmd;
    }
    public static unsafe (nint Foreground, nint Previous, nint Next) WindowState(string title)
    {
        var target = default(global::Windows.Win32.Foundation.HWND);
        WNDENUMPROC callback = (window, data) =>
        {
            Span<char> text = stackalloc char[4096];
            fixed (char* buffer = text)
            {
                var length = PInvoke.GetWindowText(window, buffer, text.Length);
                if (text[..Math.Max(0, length)].SequenceEqual(title)) target = window;
            }
            return true;
        };
        if (!PInvoke.EnumWindows(callback, default)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        return ((nint)PInvoke.GetForegroundWindow().Value,
            (nint)PInvoke.GetWindow(target, GET_WINDOW_CMD.GW_HWNDPREV).Value,
            (nint)PInvoke.GetWindow(target, GET_WINDOW_CMD.GW_HWNDNEXT).Value);
    }

    public static unsafe int ObserverWindowCount()
    {
        var count = 0;
        WNDENUMPROC callback = (window, data) =>
        {
            PInvoke.GetWindowThreadProcessId(window, out var pid);
            Span<char> name = stackalloc char[256];
            fixed (char* buffer = name)
            {
                var length = PInvoke.GetClassName(window, buffer, name.Length);
                if (pid == (uint)Environment.ProcessId && name[..Math.Max(0, length)].StartsWith("WinManager.Observer.", StringComparison.Ordinal))
                    count++;
            }
            return true;
        };
        if (!PInvoke.EnumWindows(callback, default)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        return count;
    }

    [SupportedOSPlatform("windows10.0.14393")]
    public static unsafe uint WindowDpi(string title)
    {
        uint dpi = 0;
        WNDENUMPROC callback = (window, data) =>
        {
            Span<char> text = stackalloc char[4096];
            fixed (char* buffer = text)
            {
                var length = PInvoke.GetWindowText(window, buffer, text.Length);
                if (text[..Math.Max(0, length)].SequenceEqual(title)) dpi = PInvoke.GetDpiForWindow(window);
            }
            return true;
        };
        if (!PInvoke.EnumWindows(callback, default)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        return dpi;
    }
}