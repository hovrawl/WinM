using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.UI.WindowsAndMessaging;
using PInvoke = global::Windows.Win32.TestPInvoke;

namespace WinManager.Windows.Tests;

internal static class NativeInspection
{
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