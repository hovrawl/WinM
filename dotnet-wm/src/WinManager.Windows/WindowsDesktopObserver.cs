using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using WinManager.Contracts;
using WinManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinManager.Windows;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowsDesktopObserver : IDesktopObserver
{
    private const uint CaptureMessage = 0x8001;
    private const uint StopMessage = 0x8002;
    private const uint ObjectCreate = 0x8000;
    private const uint ObjectDestroy = 0x8001;
    private readonly object gate = new();
    private readonly HashSet<string> ignoredProcesses;
    private readonly HashSet<string> ignoredClasses;
    private readonly Channel<NativeNotification> notifications = Channel.CreateBounded<NativeNotification>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private readonly Channel<CaptureRequest> captures = Channel.CreateBounded<CaptureRequest>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowIdentityMap identities = new();
    private readonly Dictionary<string, Guid> monitorIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnhookWinEventSafeHandle> hooks = [];
    private readonly WNDPROC windowProcedure;
    private readonly WINEVENTPROC eventProcedure;
    private readonly string windowClass = $"WinManager.Observer.{Guid.NewGuid():N}";
    private Thread? thread;
    private HWND notificationWindow;
    private uint nativeThreadId;
    private bool disposing;
    private bool measuringDpi;

    public WindowsDesktopObserver(IEnumerable<string>? ignoredProcessNames = null, IEnumerable<string>? ignoredClassNames = null)
    {
        ignoredProcesses = new HashSet<string>((ignoredProcessNames ?? []).Select(NormalizeProcessName), StringComparer.OrdinalIgnoreCase);
        ignoredClasses = new HashSet<string>(ignoredClassNames ?? [], StringComparer.OrdinalIgnoreCase);
        windowProcedure = WindowProcedure;
        eventProcedure = WinEventProcedure;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (thread is null)
            {
                thread = new Thread(ThreadMain) { IsBackground = true, Name = "WinManager desktop observer" };
                try
                {
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
                catch (Exception error)
                {
                    started.TrySetException(error);
                    stopped.TrySetResult();
                    captures.Writer.TryComplete(error);
                    notifications.Writer.TryComplete(error);
                    throw;
                }
            }
        }
        try { await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            await stopped.Task.ConfigureAwait(false);
            throw;
        }
    }

    public IAsyncEnumerable<NativeNotification> Notifications(CancellationToken cancellationToken) =>
        notifications.Reader.ReadAllAsync(cancellationToken);

    public async Task<DesktopSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (thread is null) throw new InvalidOperationException("StartAsync must be called before CaptureAsync.");
        }
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var request = new CaptureRequest(cancellationToken);
        using var registration = cancellationToken.Register(() => request.Completion.TrySetCanceled(cancellationToken));
        await captures.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (disposing) request.Completion.TrySetException(new ObjectDisposedException(nameof(WindowsDesktopObserver)));
            else if (!PInvoke.PostMessage(notificationWindow, CaptureMessage, default, default))
                request.Completion.TrySetException(NativeError("PostMessage"));
        }
        return await request.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!disposing)
            {
                disposing = true;
                captures.Writer.TryComplete();
                if (thread is null)
                {
                    started.TrySetException(new ObjectDisposedException(nameof(WindowsDesktopObserver)));
                    notifications.Writer.TryComplete();
                    stopped.TrySetResult();
                }
                else if (notificationWindow != default && !PInvoke.PostMessage(notificationWindow, StopMessage, default, default))
                    PInvoke.PostThreadMessage(nativeThreadId, PInvoke.WM_QUIT, default, default);
            }
        }
        await stopped.Task.ConfigureAwait(false);
    }

    private unsafe void ThreadMain()
    {
        Exception? failure = null;
        ushort atom = 0;
        HWND window = default;
        var previousDpi = default(DPI_AWARENESS_CONTEXT);
        FreeLibrarySafeHandle? instance = null;
        try
        {
            nativeThreadId = PInvoke.GetCurrentThreadId();
            previousDpi = PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previousDpi == default) throw NativeError("SetThreadDpiAwarenessContext");
            instance = PInvoke.GetModuleHandle((string?)null);
            if (instance.IsInvalid) throw NativeError("GetModuleHandle");
            fixed (char* className = windowClass)
            {
                var definition = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = windowProcedure,
                    hInstance = new HINSTANCE(instance.DangerousGetHandle()),
                    lpszClassName = className
                };
                atom = PInvoke.RegisterClassEx(definition);
                if (atom == 0) throw NativeError("RegisterClassEx");
                // A message-only window would miss WM_DISPLAYCHANGE and WM_SETTINGCHANGE broadcasts.
                window = PInvoke.CreateWindowEx(0, windowClass, "", 0, 0, 0, 0, 0, default, default, instance, null);
            }
            if (window == default) throw NativeError("CreateWindowEx");
            InstallHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND);
            InstallHook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND);
            InstallHook(PInvoke.EVENT_OBJECT_CREATE, PInvoke.EVENT_OBJECT_NAMECHANGE);
            InstallHook(PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED);
            lock (gate)
            {
                notificationWindow = window;
                if (disposing) throw new ObjectDisposedException(nameof(WindowsDesktopObserver));
                started.TrySetResult();
            }
            Notify();
            while (true)
            {
                var result = PInvoke.GetMessage(out var message, default, 0, 0);
                if (result.Value == -1) throw NativeError("GetMessage");
                if (result.Value == 0) break;
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception error)
        {
            failure = error;
            started.TrySetException(error);
        }
        finally
        {
            lock (gate) notificationWindow = default;
            foreach (var hook in hooks) hook.Dispose();
            hooks.Clear();
            if (window != default) PInvoke.DestroyWindow(window);
            if (atom != 0) PInvoke.UnregisterClass(windowClass, instance!);
            instance?.Dispose();
            if (previousDpi != default) PInvoke.SetThreadDpiAwarenessContext(previousDpi);
            captures.Writer.TryComplete(failure);
            while (captures.Reader.TryRead(out var request))
                request.Completion.TrySetException(failure ?? new ObjectDisposedException(nameof(WindowsDesktopObserver)));
            notifications.Writer.TryComplete(failure);
            started.TrySetException(failure ?? new ObjectDisposedException(nameof(WindowsDesktopObserver)));
            stopped.TrySetResult();
            GC.KeepAlive(windowProcedure);
            GC.KeepAlive(eventProcedure);
        }
    }

    private void InstallHook(uint first, uint last)
    {
        var hook = PInvoke.SetWinEventHook(first, last, default, eventProcedure, 0, 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsInvalid)
        {
            hook.Dispose();
            throw NativeError("SetWinEventHook");
        }
        hooks.Add(hook);
    }

    private unsafe void WinEventProcedure(HWINEVENTHOOK hook, uint eventType, HWND window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (eventType >= ObjectCreate && (objectId != 0 || childId != 0 || window == default)) return;
        try
        {
            if (eventType is ObjectCreate or ObjectDestroy)
                identities.RecordLifecycleChange((nint)window.Value, WindowIdentityMap.ExpandEventTime(eventTime, PInvoke.GetTickCount64()));
            Notify();
        }
        catch (Exception error)
        {
            notifications.Writer.TryComplete(error);
            PInvoke.PostQuitMessage(1);
        }
    }

    private LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        // Never allow a managed exception to unwind through a native callback.
        try
        {
            if (message == StopMessage)
            {
                PInvoke.PostQuitMessage(0);
                return default;
            }
            if (message == CaptureMessage)
            {
                // Limit each dispatch so display messages and hook callbacks cannot be starved.
                for (var count = 0; count < 8 && captures.Reader.TryRead(out var request); count++)
                {
                    if (request.Completion.Task.IsCompleted) continue;
                    try
                    {
                        request.CancellationToken.ThrowIfCancellationRequested();
                        request.Completion.TrySetResult(Capture(request.CancellationToken));
                    }
                    catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.CancellationToken); }
                    catch (Exception error) { request.Completion.TrySetException(error); }
                }
                if (captures.Reader.TryPeek(out _)) PInvoke.PostMessage(window, CaptureMessage, default, default);
                return default;
            }
            if (message is PInvoke.WM_DISPLAYCHANGE or PInvoke.WM_SETTINGCHANGE or PInvoke.WM_DEVICECHANGE ||
                (message == PInvoke.WM_DPICHANGED && !measuringDpi))
                Notify();
        }
        catch (Exception error)
        {
            notifications.Writer.TryComplete(error);
            PInvoke.PostQuitMessage(1);
        }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    private void Notify() => notifications.Writer.TryWrite(new NativeNotification(NativeNotificationKind.Reconcile));

    private unsafe DesktopSnapshot Capture(CancellationToken cancellationToken)
    {
        identities.ReconcileLifecycle();
        var monitorsByHandle = new Dictionary<HMONITOR, MonitorInfo>();
        Exception? enumerationError = null;
        MONITORENUMPROC monitorCallback = (HMONITOR monitor, HDC dc, RECT* rectangle, LPARAM data) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new MONITORINFOEXW { monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) } };
                if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) throw NativeError("GetMonitorInfo");
                var deviceName = info.szDevice.ToString();
                if (!monitorIds.TryGetValue(deviceName, out var id)) monitorIds[deviceName] = id = Guid.NewGuid();
                var dpi = ReadMonitorDpi(info.monitorInfo.rcMonitor);
                monitorsByHandle.Add(monitor, new MonitorInfo(id, deviceName, Rect(info.monitorInfo.rcMonitor), Rect(info.monitorInfo.rcWork), dpi));
                return true;
            }
            catch (Exception error) { enumerationError = error; return false; }
        };
        var monitorResult = PInvoke.EnumDisplayMonitors(default, null, monitorCallback, default);
        GC.KeepAlive(monitorCallback);
        if (enumerationError is not null) throw enumerationError;
        if (!monitorResult) throw NativeError("EnumDisplayMonitors");

        var windows = new List<WindowInfo>();
        var seen = new HashSet<nint>();
        var foreground = PInvoke.GetForegroundWindow();
        Guid? focused = null;
        WNDENUMPROC callback = (window, data) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                seen.Add((nint)window.Value);
                var candidate = ReadWindow(window, monitorsByHandle);
                if (candidate is not null)
                {
                    windows.Add(candidate);
                    if (window == foreground) focused = candidate.Id;
                }
                return true;
            }
            catch (Exception error) { enumerationError = error; return false; }
        };
        var windowResult = PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);
        if (enumerationError is not null) throw enumerationError;
        if (!windowResult) throw NativeError("EnumWindows");
        identities.Retain(seen);
        return new DesktopSnapshot(monitorsByHandle.Values.OrderBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray(),
            windows.OrderBy(window => window.Id).ToArray(), focused);
    }

    private unsafe WindowInfo? ReadWindow(HWND window, Dictionary<HMONITOR, MonitorInfo> monitors)
    {
        if (!PInvoke.IsWindowVisible(window) || window == PInvoke.GetShellWindow() || window == PInvoke.GetDesktopWindow() ||
            PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT) != window ||
            PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER) != default) return null;
        var style = (uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var extendedStyle = (uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((style & (uint)WINDOW_STYLE.WS_CHILD) != 0 ||
            (extendedStyle & ((uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE)) != 0) return null;
        var className = ReadClassName(window);
        if (className.Length == 0 || ignoredClasses.Contains(className) || IsShellOrTransientClass(className)) return null;
        uint cloaked = 0;
        if (PInvoke.DwmGetWindowAttribute(window, global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &cloaked, sizeof(uint)).Failed || cloaked != 0) return null;
        PInvoke.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;
        if (!TryReadProcess(pid, out var creationTime, out var processName) || ignoredProcesses.Contains(processName)) return null;
        var monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (!monitors.TryGetValue(monitor, out var monitorInfo)) return null;
        RECT rectangle;
        if (PInvoke.DwmGetWindowAttribute(window, global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &rectangle, (uint)sizeof(RECT)).Failed && !PInvoke.GetWindowRect(window, out rectangle)) return null;
        var title = ReadTitle(window);
        // Revalidate after the cross-process queries: an HWND may disappear or be recycled meanwhile.
        PInvoke.GetWindowThreadProcessId(window, out var currentPid);
        if (!PInvoke.IsWindow(window) || currentPid != pid ||
            !TryReadProcess(pid, out var currentCreationTime, out _) || currentCreationTime != creationTime) return null;
        var bounds = Rect(rectangle);
        var minimized = (bool)PInvoke.IsIconic(window);
        var fullscreen = !minimized && Covers(bounds, monitorInfo.Bounds);
        var id = identities.GetOrCreate(new WindowIdentity((nint)window.Value, pid, creationTime), PInvoke.GetTickCount64());
        return new WindowInfo(id, monitorInfo.Id, title, processName, className, bounds, true, minimized, fullscreen, WindowMode.Tiling);
    }

    private uint ReadMonitorDpi(RECT bounds)
    {
        // GetDpiForMonitor depends on process awareness. Probe only our PMv2 hidden window,
        // so a host with a different process-wide DPI mode still receives physical coordinates.
        measuringDpi = true;
        try
        {
            if (!PInvoke.SetWindowPos(notificationWindow, default, bounds.left, bounds.top, 1, 1,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER)) throw NativeError("SetWindowPos");
            var dpi = PInvoke.GetDpiForWindow(notificationWindow);
            if (dpi == 0) throw NativeError("GetDpiForWindow");
            return dpi;
        }
        finally { measuringDpi = false; }
    }

    private static bool IsShellOrTransientClass(string name) => name is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or
        "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow" or "#32768" or "#32770" or "tooltips_class32";

    private static string NormalizeProcessName(string name)
    {
        var fileName = Path.GetFileName(name);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
    }

    private static unsafe bool TryReadProcess(uint pid, out ulong creationTime, out string name)
    {
        creationTime = 0;
        name = "";
        using var process = PInvoke.OpenProcess_SafeHandle(global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid || !PInvoke.GetProcessTimes(process, out var created, out _, out _, out _)) return false;
        creationTime = ((ulong)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        Span<char> path = stackalloc char[32768];
        uint length = (uint)path.Length;
        if (!PInvoke.QueryFullProcessImageName(process, 0, path, ref length)) return false;
        name = NormalizeProcessName(new string(path[..(int)length]));
        return true;
    }

    private static unsafe string ReadClassName(HWND window)
    {
        Span<char> text = stackalloc char[256];
        fixed (char* buffer = text)
        {
            var length = PInvoke.GetClassName(window, buffer, text.Length);
            return length > 0 ? new string(text[..length]) : "";
        }
    }

    private static unsafe string ReadTitle(HWND window)
    {
        Span<char> text = stackalloc char[4096];
        fixed (char* buffer = text)
        {
            var length = PInvoke.GetWindowText(window, buffer, text.Length);
            return length > 0 ? new string(text[..length]) : "";
        }
    }

    private static PixelRect Rect(RECT rect) => new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    private static bool Covers(PixelRect window, PixelRect monitor) =>
        window.X <= monitor.X && window.Y <= monitor.Y &&
        (long)window.X + window.Width >= (long)monitor.X + monitor.Width &&
        (long)window.Y + window.Height >= (long)monitor.Y + monitor.Height;
    private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation);

    private sealed record CaptureRequest(CancellationToken CancellationToken)
    {
        public TaskCompletionSource<DesktopSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}