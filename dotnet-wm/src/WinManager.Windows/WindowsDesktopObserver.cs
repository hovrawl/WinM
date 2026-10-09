using System.ComponentModel;
using System.Runtime.CompilerServices;
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
public sealed partial class WindowsDesktopObserver : IWindowPlatform
{
    private const uint CaptureMessage = 0x8001;
    private const uint StopMessage = 0x8002;
    private const uint ApplyMessage = 0x8003;
    private const uint RecoveryCheckMessage = 0x8004;
    private const uint ObjectCreate = 0x8000;
    private const uint ObjectDestroy = 0x8001;
    private readonly object gate = new();
    private readonly HashSet<string> ignoredProcesses;
    private readonly HashSet<string> ignoredClasses;
    private readonly Channel<NativeNotification> notifications = Channel.CreateUnbounded<NativeNotification>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private int reconcilePending;
    private readonly Dictionary<nint, Guid> movingWindows = [];
    private readonly HashSet<Guid> pendingDrops = [];
    private readonly Channel<CaptureRequest> captures = Channel.CreateBounded<CaptureRequest>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Channel<ApplyRequest> applies = Channel.CreateBounded<ApplyRequest>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Dictionary<Guid, WindowInfo> observedWindows = [];
    private readonly Dictionary<Guid, RecoveryRecord> hiddenWindows = [];
    private readonly HashSet<Guid> issuedHides = [];
    private readonly RecoveryConnection recovery = new();
    private readonly string recoveryExecutablePath;
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
    internal int? RecoveryHelperProcessId => recovery.HelperProcessId;

    public WindowsDesktopObserver(IEnumerable<string>? ignoredProcessNames = null, IEnumerable<string>? ignoredClassNames = null,
        string? recoveryExecutablePath = null)
    {
        ignoredProcesses = new HashSet<string>((ignoredProcessNames ?? []).Select(NormalizeProcessName), StringComparer.OrdinalIgnoreCase);
        ignoredClasses = new HashSet<string>(ignoredClassNames ?? [], StringComparer.OrdinalIgnoreCase);
        this.recoveryExecutablePath = recoveryExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "WinManager.Recovery.exe");
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
                    applies.Writer.TryComplete(error);
                    CompleteShortcuts(error);
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

    public async IAsyncEnumerable<NativeNotification> Notifications([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken))
        {
            if (notification.Kind == NativeNotificationKind.Reconcile) Interlocked.Exchange(ref reconcilePending, 0);
            yield return notification;
        }
    }

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

    public Task<ApplyResult> ApplyAsync(LayoutPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DispatchApplyAsync(new LayoutPlan(plan.Placements.ToArray(), plan.FocusWindowId,
            plan.Visibility?.ToArray(), plan.CompletedMoves?.ToArray()), false, cancellationToken);
    }

    public Task<ApplyResult> RestoreAsync(CancellationToken cancellationToken) =>
        DispatchApplyAsync(new LayoutPlan([]), true, cancellationToken);

    private async Task<ApplyResult> DispatchApplyAsync(LayoutPlan plan, bool restore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (thread is null) throw new InvalidOperationException("StartAsync must be called before ApplyAsync.");
        }
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var request = new ApplyRequest(plan, cancellationToken, restore);
        using var registration = cancellationToken.Register(() => request.Completion.TrySetCanceled(cancellationToken));
        await applies.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (disposing) request.Completion.TrySetException(new ObjectDisposedException(nameof(WindowsDesktopObserver)));
            else if (!PInvoke.PostMessage(notificationWindow, ApplyMessage, default, default))
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
                applies.Writer.TryComplete();
                shortcutRequests.Writer.TryComplete();
                if (thread is null)
                {
                    started.TrySetException(new ObjectDisposedException(nameof(WindowsDesktopObserver)));
                    CompleteShortcuts(null);
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
        Timer? recoveryTimer = null;
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
            InstallHook(PInvoke.EVENT_SYSTEM_MOVESIZESTART, PInvoke.EVENT_SYSTEM_MOVESIZEEND);
            InstallHook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND);
            InstallHook(PInvoke.EVENT_OBJECT_CREATE, PInvoke.EVENT_OBJECT_NAMECHANGE);
            InstallHook(PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED);
            // Start recovery before initial capture, so an abandoned journal is recovered even
            // when all managed windows were left hidden by a previous host.
            try { recovery.Start(recoveryExecutablePath); }
            catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or
                UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
            lock (gate)
            {
                notificationWindow = window;
                if (disposing) throw new ObjectDisposedException(nameof(WindowsDesktopObserver));
                started.TrySetResult();
            }
            Notify();
            recoveryTimer = new Timer(_ =>
            {
                lock (gate)
                    if (notificationWindow != default) PInvoke.PostMessage(notificationWindow, RecoveryCheckMessage, default, default);
            }, null, 100, 100);
            while (true)
            {
                var result = PInvoke.GetMessage(out var message, default, 0, 0);
                if (result.Value == -1) throw NativeError("GetMessage");
                if (result.Value == 0) break;
                // Cross-process operations run in the loop, never inside a native callback.
                if (message.hwnd == window && message.message == CaptureMessage)
                {
                    ProcessCaptures(window);
                    continue;
                }
                if (message.hwnd == window && message.message == ApplyMessage)
                {
                    ProcessApplies(window);
                    continue;
                }
                if (message.hwnd == window && message.message == RecoveryCheckMessage)
                {
                    CheckRecovery();
                    continue;
                }
                if (message.hwnd == window && message.message == RegisterShortcutsMessage)
                {
                    ProcessShortcutRequests(window);
                    continue;
                }
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
            recoveryTimer?.Dispose();
            ReleaseShortcuts(window, []);
            CompleteShortcuts(failure);
            try { RestoreManaged(CancellationToken.None, true); }
            catch { /* Leave the durable helper journal intact on a failed final restore. */ }
            recovery.Dispose();
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
            applies.Writer.TryComplete(failure);
            while (applies.Reader.TryRead(out var request))
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
            var handle = (nint)window.Value;
            if (eventType == PInvoke.EVENT_SYSTEM_MOVESIZESTART && window != default)
            {
                var id = observedWindows.Keys.FirstOrDefault(id => identities.TryResolve(id, out var identity) && identity.Handle == handle);
                if (id != Guid.Empty && movingWindows.TryAdd(handle, id))
                    notifications.Writer.TryWrite(new(NativeNotificationKind.MoveStarted, id, observedWindows[id].Bounds));
            }
            else if (eventType == PInvoke.EVENT_SYSTEM_MOVESIZEEND && movingWindows.Remove(handle, out var id))
            {
                pendingDrops.Add(id);
                notifications.Writer.TryWrite(new(NativeNotificationKind.MoveEnded, id));
            }
            else if (eventType == ObjectDestroy)
            {
                if (movingWindows.Remove(handle, out var destroyed)) pendingDrops.Remove(destroyed);
            }
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
            if (message == PInvoke.WM_HOTKEY)
            {
                QueueShortcut(wParam, lParam);
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

    private void Notify()
    {
        if (Interlocked.Exchange(ref reconcilePending, 1) == 0)
            notifications.Writer.TryWrite(new NativeNotification(NativeNotificationKind.Reconcile));
    }

    private void ProcessCaptures(HWND window)
    {
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
    }

    private void ProcessApplies(HWND window)
    {
        // One plan per dispatch leaves opportunities for lifecycle events between plans.
        if (applies.Reader.TryRead(out var request) && !request.Completion.Task.IsCompleted)
        {
            try
            {
                lock (gate) ObjectDisposedException.ThrowIf(disposing, this);
                request.Completion.TrySetResult(request.Restore
                    ? RestoreManaged(request.CancellationToken, true)
                    : Apply(request.Plan, request.CancellationToken));
            }
            catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.CancellationToken); }
            catch (Exception error) { request.Completion.TrySetException(error); }
        }
        if (applies.Reader.TryPeek(out _)) PInvoke.PostMessage(window, ApplyMessage, default, default);
    }

    private unsafe DesktopSnapshot Capture(CancellationToken cancellationToken)
    {
        CheckRecovery();
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
        var handles = new List<HWND>();
        var foreground = PInvoke.GetForegroundWindow();
        Guid? focused = null;
        WNDENUMPROC callback = (window, data) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                seen.Add((nint)window.Value);
                handles.Add(window);
                return true;
            }
            catch (Exception error) { enumerationError = error; return false; }
        };
        var windowResult = PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);
        if (enumerationError is not null) throw enumerationError;
        if (!windowResult) throw NativeError("EnumWindows");
        foreach (var window in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ReadWindow(window, monitorsByHandle);
            if (candidate is null) continue;
            windows.Add(candidate);
            if (window == foreground) focused = candidate.Id;
        }
        identities.Retain(seen);
        observedWindows.Clear();
        foreach (var candidate in windows) observedWindows.Add(candidate.Id, candidate);
        return new DesktopSnapshot(monitorsByHandle.Values.OrderBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray(),
            windows.OrderBy(window => window.Id).ToArray(), focused);
    }

    private unsafe WindowInfo? ReadWindow(HWND window, Dictionary<HMONITOR, MonitorInfo> monitors)
    {
        var managerHidden = hiddenWindows.Any(pair => issuedHides.Contains(pair.Key) &&
            pair.Value.Handle == (long)(nint)window.Value && RecoveryNative.Matches(pair.Value));
        if ((!PInvoke.IsWindowVisible(window) && !managerHidden) || window == PInvoke.GetShellWindow() || window == PInvoke.GetDesktopWindow() ||
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
        var minimum = ReadMinimumSize(window, rectangle);
        // Revalidate after the cross-process queries: an HWND may disappear or be recycled meanwhile.
        PInvoke.GetWindowThreadProcessId(window, out var currentPid);
        if (!PInvoke.IsWindow(window) || currentPid != pid ||
            !TryReadProcess(pid, out var currentCreationTime, out _) || currentCreationTime != creationTime) return null;
        var bounds = Rect(rectangle);
        var minimized = (bool)PInvoke.IsIconic(window);
        var fullscreen = !minimized && Covers(bounds, monitorInfo.Bounds);
        var id = identities.GetOrCreate(new WindowIdentity((nint)window.Value, pid, creationTime), PInvoke.GetTickCount64());
        return new WindowInfo(id, monitorInfo.Id, title, processName, className, bounds, PInvoke.IsWindowVisible(window), minimized, fullscreen,
            WindowMode.Tiling, minimum.Width, minimum.Height, managerHidden);
    }

    private unsafe ApplyResult Apply(LayoutPlan plan, CancellationToken cancellationToken)
    {
        CheckRecovery();
        foreach (var id in plan.CompletedMoves ?? []) pendingDrops.Remove(id);
        var failures = new List<NativeActionFailure>();
        foreach (var visibility in plan.Visibility ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (movingWindows.ContainsValue(visibility.WindowId) || pendingDrops.Contains(visibility.WindowId)) continue;
            ApplyVisibility(visibility, failures, cancellationToken);
        }
        foreach (var placement in plan.Placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (movingWindows.ContainsValue(placement.WindowId) || pendingDrops.Contains(placement.WindowId)) continue;
            if (!ValidateTarget(placement.WindowId, out var window, out _, out var failure))
            {
                failures.Add(failure!);
                continue;
            }
            if (placement.Bounds.Width <= 0 || placement.Bounds.Height <= 0)
            {
                failures.Add(new(placement.WindowId, "invalid_bounds", "Window dimensions must be positive."));
                continue;
            }
            if (!PInvoke.GetWindowRect(window, out var outer))
            {
                failures.Add(ActionError(placement.WindowId, "GetWindowRect"));
                continue;
            }
            var frame = outer;
            PInvoke.DwmGetWindowAttribute(window, global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                &frame, (uint)sizeof(RECT));
            // Contract bounds describe the visible frame; SetWindowPos includes invisible borders.
            var x = (long)placement.Bounds.X + outer.left - frame.left;
            var y = (long)placement.Bounds.Y + outer.top - frame.top;
            var outerWidth = (long)placement.Bounds.Width + outer.right - outer.left - (frame.right - (long)frame.left);
            var outerHeight = (long)placement.Bounds.Height + outer.bottom - outer.top - (frame.bottom - (long)frame.top);
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue ||
                outerWidth <= 0 || outerWidth > int.MaxValue || outerHeight <= 0 || outerHeight > int.MaxValue)
            {
                failures.Add(new(placement.WindowId, "invalid_bounds", "Window bounds exceed native coordinate limits."));
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck the GUID generation as well as PID/start time before issuing the native request.
            if (!ValidateTarget(placement.WindowId, out window, out _, out failure))
            {
                failures.Add(failure!);
                continue;
            }
            if (movingWindows.ContainsValue(placement.WindowId) || pendingDrops.Contains(placement.WindowId)) continue;
            Marshal.SetLastPInvokeError(0);
            if (!PInvoke.SetWindowPos(window, default, (int)x, (int)y, (int)outerWidth, (int)outerHeight,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS))
                failures.Add(ActionError(placement.WindowId, "SetWindowPos"));
        }
        if (plan.FocusWindowId is { } focusId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidateTarget(focusId, out var window, out _, out var failure)) failures.Add(failure!);
            else
            {
                Marshal.SetLastPInvokeError(0);
                PInvoke.SetForegroundWindow(window);
                var error = Marshal.GetLastWin32Error();
                if (!ValidateTarget(focusId, out _, out _, out failure)) failures.Add(failure!);
                else if (PInvoke.GetForegroundWindow() != window)
                    failures.Add(new(focusId, error == 5 ? "access_denied" : "focus_denied",
                        "Windows did not grant foreground focus."));
            }
        }
        Notify();
        return new ApplyResult(failures);
    }

    private unsafe bool ValidateTarget(Guid id, out HWND window, out WindowInfo? info, out NativeActionFailure? failure)
    {
        window = default;
        info = null;
        failure = new(id, "stale_window", "Window identity is no longer current.");
        if (!observedWindows.TryGetValue(id, out info) || !identities.TryResolve(id, out var identity)) return false;
        window = new HWND(identity.Handle);
        PInvoke.GetWindowThreadProcessId(window, out var pid);
        if (!PInvoke.IsWindow(window) || pid != identity.ProcessId) return false;
        if (!TryReadProcess(pid, out var creationTime, out _))
        {
            failure = new(id, "access_denied", "Cannot verify the target process identity.");
            return false;
        }
        if (creationTime != identity.ProcessCreationTime || !identities.TryResolve(id, out var current) || current != identity) return false;
        uint cloaked = 0;
        var style = (uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var normalStyle = (uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var className = ReadClassName(window);
        var managerHidden = issuedHides.Contains(id) && hiddenWindows.TryGetValue(id, out var hidden) && RecoveryNative.Matches(hidden);
        if ((!PInvoke.IsWindowVisible(window) && !managerHidden) || window == PInvoke.GetShellWindow() || window == PInvoke.GetDesktopWindow() ||
            PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT) != window ||
            (normalStyle & (uint)WINDOW_STYLE.WS_CHILD) != 0 ||
            className.Length == 0 || ignoredClasses.Contains(className) || IsShellOrTransientClass(className) ||
            PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER) != default ||
            (style & ((uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE)) != 0 ||
            PInvoke.DwmGetWindowAttribute(window, global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
                &cloaked, sizeof(uint)).Failed || cloaked != 0)
        {
            failure = new(id, "ineligible_window", "Window is no longer eligible for managed actions.");
            return false;
        }
        PInvoke.GetWindowThreadProcessId(window, out var finalPid);
        if (!PInvoke.IsWindow(window) || finalPid != identity.ProcessId ||
            !identities.TryResolve(id, out current) || current != identity) return false;
        failure = null;
        return true;
    }

    private void CheckRecovery()
    {
        if (hiddenWindows.Count != 0 && !recovery.Healthy)
            RestoreManaged(CancellationToken.None, false);
    }

    private unsafe void ApplyVisibility(WindowVisibility visibility, List<NativeActionFailure> failures, CancellationToken token)
    {
        if (!ValidateTarget(visibility.WindowId, out var window, out _, out var failure))
        {
            failures.Add(failure!);
            return;
        }
        if (visibility.Visible)
        {
            if (hiddenWindows.TryGetValue(visibility.WindowId, out var original))
                RestoreOne(visibility.WindowId, original, failures);
            return;
        }
        if (hiddenWindows.ContainsKey(visibility.WindowId)) return;
        if (PInvoke.IsIconic(window))
        {
            failures.Add(new(visibility.WindowId, "ineligible_window", "Application-minimized windows are not manager-hidden."));
            return;
        }
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.GetWindowPlacement(window, ref placement) || !identities.TryResolve(visibility.WindowId, out var identity))
        {
            failures.Add(ActionError(visibility.WindowId, "GetWindowPlacement"));
            return;
        }
        var command = placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED
            ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED : SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE;
        var record = new RecoveryRecord((long)(nint)window.Value, identity.ProcessId, identity.ProcessCreationTime,
            (uint)command, "WinManager.Recovery." + Guid.NewGuid().ToString("N"));
        try
        {
            recovery.Start(recoveryExecutablePath);
            if (!ValidateTarget(visibility.WindowId, out window, out _, out failure))
            {
                failures.Add(failure!);
                return;
            }
            if (!RecoveryNative.Mark(record))
            {
                failures.Add(ActionError(visibility.WindowId, "SetProp"));
                return;
            }
            hiddenWindows.Add(visibility.WindowId, record);
            recovery.Send(new RecoveryCommand("register", record));
        }
        catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            failures.Add(new(visibility.WindowId, "recovery_unavailable", error.Message));
            if (hiddenWindows.ContainsKey(visibility.WindowId)) RestoreOne(visibility.WindowId, record with { HideConfirmed = true }, failures);
            CheckRecovery();
            return;
        }
        if (token.IsCancellationRequested)
        {
            RestoreOne(visibility.WindowId, record with { HideConfirmed = true }, failures);
            token.ThrowIfCancellationRequested();
        }
        if (!ValidateTarget(visibility.WindowId, out window, out _, out failure) || !RecoveryNative.Matches(record))
        {
            failures.Add(failure ?? new(visibility.WindowId, "stale_window", "Target changed during recovery registration."));
            RestoreOne(visibility.WindowId, record with { HideConfirmed = true }, failures);
            return;
        }
        if (!recovery.Healthy)
        {
            failures.Add(new(visibility.WindowId, "recovery_unavailable", "Recovery helper exited before hiding."));
            RestoreOne(visibility.WindowId, record with { HideConfirmed = true }, failures);
            return;
        }
        if (!PInvoke.ShowWindowAsync(window, SHOW_WINDOW_CMD.SW_HIDE))
        {
            failures.Add(ActionError(visibility.WindowId, "ShowWindowAsync"));
            RestoreOne(visibility.WindowId, record with { HideConfirmed = true }, failures);
            return;
        }
        issuedHides.Add(visibility.WindowId);
        for (var attempt = 0; attempt < 50 && RecoveryNative.Matches(record) && PInvoke.IsWindowVisible(window); attempt++)
            Thread.Sleep(10);
        if (!RecoveryNative.Matches(record) || PInvoke.IsWindowVisible(window))
        {
            failures.Add(new(visibility.WindowId, "visibility_denied", "Window did not become hidden."));
            RestoreOne(visibility.WindowId, record, failures);
        }
        else
        {
            record = record with { HideConfirmed = true };
            hiddenWindows[visibility.WindowId] = record;
            try { recovery.Send(new RecoveryCommand("confirmed", record)); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                failures.Add(new(visibility.WindowId, "recovery_unavailable", error.Message));
                RestoreOne(visibility.WindowId, record, failures);
                CheckRecovery();
            }
        }
        Notify();
    }

    private void RestoreOne(Guid id, RecoveryRecord record, List<NativeActionFailure> failures)
    {
        if (issuedHides.Contains(id) && !RecoveryNative.Restore(record))
        {
            failures.Add(new(id, "restore_failed", "Original window visibility was not restored; recovery record retained."));
            return;
        }
        // Never retire durable protection before actual native restoration has succeeded.
        if (recovery.Healthy)
        {
            try { recovery.Send(new RecoveryCommand("remove", record)); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                failures.Add(new(id, "recovery_unavailable", error.Message));
            }
        }
        RecoveryNative.Unmark(record);
        hiddenWindows.Remove(id);
        issuedHides.Remove(id);
        Notify();
    }

    private ApplyResult RestoreManaged(CancellationToken token, bool complete)
    {
        var failures = new List<NativeActionFailure>();
        foreach (var pair in hiddenWindows.ToArray())
        {
            token.ThrowIfCancellationRequested();
            RestoreOne(pair.Key, pair.Value, failures);
        }
        if (complete && hiddenWindows.Count == 0 && failures.Count == 0 && recovery.Healthy)
        {
            try { recovery.Complete(); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                failures.Add(new(null, "recovery_unavailable", error.Message));
            }
        }
        return new ApplyResult(failures);
    }

    private static NativeActionFailure ActionError(Guid id, string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new(id, error switch { 5 => "access_denied", 1400 => "stale_window", 1460 => "timeout", _ => "native_error" },
            $"{operation} failed ({error}).");
    }

    private static unsafe (int Width, int Height) ReadMinimumSize(HWND window, RECT frame)
    {
        var dpi = PInvoke.GetDpiForWindow(window);
        if (dpi == 0) dpi = 96;
        var minimum = new MINMAXINFO
        {
            ptMinTrackSize = new System.Drawing.Point
            {
                X = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXMINTRACK, dpi),
                Y = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CYMINTRACK, dpi)
            }
        };
        var fallback = minimum.ptMinTrackSize;
        // A hung or inaccessible application must not stall the observer indefinitely.
        if (PInvoke.SendMessageTimeout(window, PInvoke.WM_GETMINMAXINFO, default, new LPARAM((nint)(&minimum)),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ERRORONEXIT,
            50, null) == default) minimum.ptMinTrackSize = fallback;
        if (!PInvoke.GetWindowRect(window, out var outer)) return (Math.Max(1, minimum.ptMinTrackSize.X), Math.Max(1, minimum.ptMinTrackSize.Y));
        var borderWidth = (long)outer.right - outer.left - (frame.right - (long)frame.left);
        var borderHeight = (long)outer.bottom - outer.top - (frame.bottom - (long)frame.top);
        return ((int)Math.Clamp(minimum.ptMinTrackSize.X - borderWidth, 1, int.MaxValue),
            (int)Math.Clamp(minimum.ptMinTrackSize.Y - borderHeight, 1, int.MaxValue));
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

    private sealed record ApplyRequest(LayoutPlan Plan, CancellationToken CancellationToken, bool Restore)
    {
        public TaskCompletionSource<ApplyResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}