using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using PInvoke = global::Windows.Win32.TestPInvoke;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
internal static class TestWindowProgram
{
    private const uint CommandMessage = 0x8003;
    private static readonly ConcurrentQueue<string> commands = new();
    private static readonly Dictionary<string, HWND> windows = [];
    private static readonly WNDPROC procedure = WindowProcedure;
    private static string prefix = "";
    private static string windowClass = "";
    private static string ignoredClass = "";
    private static FreeLibrarySafeHandle? instance;
    private static bool slowMinimum;
    private static bool impossibleMinimum;

    [STAThread]
    public static unsafe int Main(string[] args)
    {
        if (args.Length == 3 && args[0].StartsWith("WinManager.Recovery.", StringComparison.Ordinal) &&
            Environment.GetEnvironmentVariable("WINMANAGER_TEST_RECOVERY_NO_ACK") == "1" &&
            Environment.GetEnvironmentVariable("WINMANAGER_ISOLATED_INTERACTIVE") == "1")
            return RecoveryTestHost.RefuseAcknowledgmentAsync(args[0]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--recovery-test-host" &&
            Environment.GetEnvironmentVariable("WINMANAGER_ISOLATED_INTERACTIVE") == "1" && Environment.UserInteractive)
            return RecoveryTestHost.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length != 2 || args[0] is not ("--native-window-fixture" or "--native-layout-fixture") ||
            Environment.GetEnvironmentVariable("WINMANAGER_ISOLATED_INTERACTIVE") != "1" || !Environment.UserInteractive)
            return 2;
        prefix = args[1];
        windowClass = prefix + ".Window";
        ignoredClass = prefix + ".Ignored";
        var previous = PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == default) return 3;
        instance = PInvoke.GetModuleHandle((string?)null);
        var registered = new List<string>();
        try
        {
            foreach (var name in new[] { windowClass, ignoredClass })
            {
                fixed (char* className = name)
                {
                    var definition = new WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                        hInstance = new HINSTANCE(instance.DangerousGetHandle()),
                        lpfnWndProc = procedure,
                        lpszClassName = className
                    };
                    if (PInvoke.RegisterClassEx(definition) == 0) throw NativeError();
                }
                registered.Add(name);
            }
            Create("normal");
            Create("hidden", visible: false);
            if (args[0] != "--native-layout-fixture")
            {
                Create("popup", style: WINDOW_STYLE.WS_POPUP);
                Create("tool", WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
                Create("owned", owner: windows["normal"]);
                Create("noactivate", WINDOW_EX_STYLE.WS_EX_NOACTIVATE);
                Create("child", owner: windows["normal"], style: WINDOW_STYLE.WS_CHILD);
                Create("ignored", className: ignoredClass);
            }
            var control = windows["hidden"];
            var nativeThread = PInvoke.GetCurrentThreadId();
            // Force creation of the message queue before announcing readiness.
            if (!PInvoke.PostMessage(control, CommandMessage, default, default)) throw NativeError();
            var input = new Thread(() =>
            {
                while (Console.ReadLine() is { } command)
                {
                    commands.Enqueue(command);
                    if (!PInvoke.PostMessage(control, CommandMessage, default, default)) break;
                }
                PInvoke.PostThreadMessage(nativeThread, PInvoke.WM_QUIT, default, default);
            }) { IsBackground = true };
            input.Start();
            Console.WriteLine("READY");
            while (true)
            {
                var result = PInvoke.GetMessage(out var message, default, 0, 0);
                if (result.Value == -1) throw NativeError();
                if (result.Value == 0) break;
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (uint.TryParse(Environment.GetEnvironmentVariable("WINMANAGER_TEST_FOREGROUND_PARENT"), out var parentPid))
                PInvoke.AllowSetForegroundWindow(parentPid);
            foreach (var window in windows.Values.Reverse()) PInvoke.DestroyWindow(window);
            windows.Clear();
            foreach (var name in registered) PInvoke.UnregisterClass(name, instance);
            instance.Dispose();
            PInvoke.SetThreadDpiAwarenessContext(previous);
            GC.KeepAlive(procedure);
        }
    }

    private static unsafe void Create(string name, WINDOW_EX_STYLE extendedStyle = 0, HWND owner = default, bool visible = true,
        string? className = null, WINDOW_STYLE style = WINDOW_STYLE.WS_OVERLAPPEDWINDOW)
    {
        var window = PInvoke.CreateWindowEx(extendedStyle, className ?? windowClass, prefix + "." + name,
            style, 100, 100, 400, 300, owner, default, instance!, null);
        if (window == default) throw NativeError();
        windows[name] = window;
        if (visible) PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    private static unsafe LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_GETMINMAXINFO)
        {
            if (slowMinimum) Thread.Sleep(500);
            var result = PInvoke.DefWindowProc(window, message, wParam, lParam);
            var minimum = (MINMAXINFO*)lParam.Value;
            minimum->ptMinTrackSize.X = Math.Max(minimum->ptMinTrackSize.X, impossibleMinimum ? 1_000_000 : 340);
            minimum->ptMinTrackSize.Y = Math.Max(minimum->ptMinTrackSize.Y, impossibleMinimum ? 1_000_000 : 240);
            return result;
        }
        if (message != CommandMessage) return PInvoke.DefWindowProc(window, message, wParam, lParam);
        try
        {
            while (commands.TryDequeue(out var command))
            {
                switch (command)
                {
                    case "quit": PInvoke.PostQuitMessage(0); break;
                    case "rename":
                        if (!PInvoke.SetWindowText(windows["normal"], prefix + ".renamed")) throw NativeError();
                        break;
                    case "move":
                        if (!PInvoke.SetWindowPos(windows["normal"], default, 220, 230, 500, 350,
                            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER)) throw NativeError();
                        break;
                    case "move-start":
                        PInvoke.NotifyWinEvent(PInvoke.EVENT_SYSTEM_MOVESIZESTART, windows["normal"], 0, 0);
                        break;
                    case "move-end":
                        PInvoke.NotifyWinEvent(PInvoke.EVENT_SYSTEM_MOVESIZEEND, windows["normal"], 0, 0);
                        break;
                    case "minimize": PInvoke.ShowWindow(windows["normal"], SHOW_WINDOW_CMD.SW_MINIMIZE); break;
                    case "maximize": PInvoke.ShowWindow(windows["normal"], SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED); break;
                    case "fullscreen":
                        var monitor = PInvoke.MonitorFromWindow(windows["normal"], MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                        if (!PInvoke.GetMonitorInfo(monitor, ref info)) throw NativeError();
                        PInvoke.SetWindowLong(windows["normal"], WINDOW_LONG_PTR_INDEX.GWL_STYLE,
                            unchecked((int)(WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE)));
                        var bounds = info.rcMonitor;
                        if (!PInvoke.SetWindowPos(windows["normal"], default, bounds.left, bounds.top,
                            bounds.right - bounds.left, bounds.bottom - bounds.top,
                            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER)) throw NativeError();
                        break;
                    case "restore": PInvoke.ShowWindow(windows["normal"], SHOW_WINDOW_CMD.SW_RESTORE); break;
                    case "hide": PInvoke.ShowWindow(windows["normal"], SHOW_WINDOW_CMD.SW_HIDE); break;
                    case "show": PInvoke.ShowWindow(windows["normal"], SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE); break;
                    case "focus":
                        if (!PInvoke.SetForegroundWindow(windows["normal"])) throw NativeError();
                        break;
                    case "destroy":
                        if (!PInvoke.DestroyWindow(windows["normal"])) throw NativeError();
                        windows.Remove("normal");
                        windows.Remove("owned");
                        windows.Remove("child");
                        break;
                    case "create": Create("normal"); break;
                    case "slow-minimum": slowMinimum = true; break;
                    case "impossible-minimum": impossibleMinimum = true; break;
                    case "cloak":
                        int cloak = 1;
                        PInvoke.DwmSetWindowAttribute(windows["normal"], global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
                            &cloak, sizeof(int)).ThrowOnFailure();
                        break;
                    case "uncloak":
                        int uncloak = 0;
                        PInvoke.DwmSetWindowAttribute(windows["normal"], global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
                            &uncloak, sizeof(int)).ThrowOnFailure();
                        break;
                    case "display":
                        // Send synthetic broadcasts without changing the actual display configuration.
                        PInvoke.PostMessage(new HWND((nint)0xffff), PInvoke.WM_DISPLAYCHANGE, default, default);
                        PInvoke.PostMessage(new HWND((nint)0xffff), PInvoke.WM_SETTINGCHANGE, default, default);
                        break;
                    default: throw new InvalidOperationException(command);
                }
                Console.WriteLine("OK");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.WriteLine("ERROR");
            PInvoke.PostQuitMessage(1);
        }
        return default;
    }

    private static Win32Exception NativeError() => new(Marshal.GetLastWin32Error());
}