using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using Microsoft.Extensions.Hosting;

namespace HomeDeskSpace;

internal interface IHostTray
{
    Task RunAsync(Action requestShutdown, HostTrayVisibility visibility, CancellationToken stoppingToken);
}

internal sealed class HostTrayVisibility
{
    private int managerCount;

    public bool IsVisible => Volatile.Read(ref managerCount) == 0;

    public event Action? VisibilityChanged;

    public IDisposable RegisterManager()
    {
        if (Interlocked.Increment(ref managerCount) == 1) VisibilityChanged?.Invoke();
        return new ManagerLease(this);
    }

    private void ReleaseManager()
    {
        if (Interlocked.Decrement(ref managerCount) == 0) VisibilityChanged?.Invoke();
    }

    private sealed class ManagerLease(HostTrayVisibility visibility) : IDisposable
    {
        private HostTrayVisibility? owner = visibility;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseManager();
    }
}

internal sealed class HostTrayService(
    IHostApplicationLifetime lifetime,
    IHostTray tray,
    HostTrayVisibility visibility) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        tray.RunAsync(lifetime.StopApplication, visibility, stoppingToken);
}

[SupportedOSPlatform("windows10.0.15063")]
internal sealed class WindowsHostTray : IHostTray
{
    public Task RunAsync(Action requestShutdown, HostTrayVisibility visibility, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(requestShutdown);
        ArgumentNullException.ThrowIfNull(visibility);
        if (stoppingToken.IsCancellationRequested) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (var loop = new TrayLoop(requestShutdown, visibility)) loop.Run(stoppingToken);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "HomeDeskSpace host tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class TrayLoop : IDisposable
    {
        private const uint CallbackMessage = 0x8001;
        private const uint StopMessage = 0x8002;
        private const uint VisibilityMessage = 0x8003;
        private const uint IconId = 1;
        private const int QuitCommand = 1;
        private readonly Action requestShutdown;
        private readonly HostTrayVisibility visibility;
        private readonly WNDPROC windowProcedure;
        private readonly string className = "HomeDesk.Tray." + Guid.NewGuid().ToString("N");
        private readonly uint nativeThreadId = PInvoke.GetCurrentThreadId();
        private FreeLibrarySafeHandle? instance;
        private HWND window;
        private ushort atom;
        private uint taskbarCreatedMessage;
        private bool iconAdded;
        private Exception? callbackFailure;

        public TrayLoop(Action requestShutdown, HostTrayVisibility visibility)
        {
            this.requestShutdown = requestShutdown;
            this.visibility = visibility;
            windowProcedure = WindowProcedure;
        }

        public unsafe void Run(CancellationToken stoppingToken)
        {
            _ = PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
            instance = PInvoke.GetModuleHandle((string?)null);
            if (instance.IsInvalid) throw NativeError("GetModuleHandle");
            taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreatedMessage == 0) throw NativeError("RegisterWindowMessage");
            fixed (char* name = className)
            {
                var definition = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = windowProcedure,
                    hInstance = new HINSTANCE(instance.DangerousGetHandle()),
                    lpszClassName = name
                };
                atom = PInvoke.RegisterClassEx(definition);
                if (atom == 0) throw NativeError("RegisterClassEx");
                // A message-only window would miss Explorer's TaskbarCreated broadcast.
                window = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className,
                    "HomeDeskSpace host tray", WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0,
                    default, default, instance, null);
            }

            if (window == default) throw NativeError("CreateWindowEx");
            visibility.VisibilityChanged += RequestVisibilityRefresh;
            UpdateIconVisibility();
            using var registration = stoppingToken.Register(() =>
            {
                if (!PInvoke.PostMessage(window, StopMessage, default, default))
                    PInvoke.PostThreadMessage(nativeThreadId, PInvoke.WM_QUIT, default, default);
            });
            while (true)
            {
                var result = PInvoke.GetMessage(out var message, default, 0, 0);
                if (result.Value == -1) throw NativeError("GetMessage");
                if (result.Value == 0) break;
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
                if (callbackFailure is { } error) throw error;
            }

            if (callbackFailure is { } failure) throw failure;
        }

        private void AddIcon()
        {
            if (iconAdded) return;
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = window,
                uID = IconId,
                uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON |
                         NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
                uCallbackMessage = CallbackMessage,
                hIcon = PInvoke.LoadIcon(default(HINSTANCE), PInvoke.IDI_APPLICATION)
            };
            const string tooltip = "HomeDeskSpace";
            for (var index = 0; index < tooltip.Length; index++) data.szTip[index] = tooltip[index];
            if (data.hIcon == default || !PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
                throw new InvalidOperationException("Could not add the HomeDeskSpace notification-area icon.");
            iconAdded = true;
        }

        private void RemoveIcon()
        {
            if (!iconAdded || window == default) return;
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = window,
                uID = IconId
            };
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            iconAdded = false;
        }

        private void RequestVisibilityRefresh()
        {
            if (window != default) PInvoke.PostMessage(window, VisibilityMessage, default, default);
        }

        private void UpdateIconVisibility()
        {
            if (visibility.IsVisible) AddIcon();
            else RemoveIcon();
        }

        private LRESULT WindowProcedure(HWND handle, uint message, WPARAM wParam, LPARAM lParam)
        {
            try
            {
                if (message == StopMessage)
                {
                    PInvoke.EndMenu();
                    PInvoke.PostQuitMessage(0);
                    return default;
                }

                if (message == VisibilityMessage)
                {
                    UpdateIconVisibility();
                    return default;
                }

                if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage)
                {
                    iconAdded = false;
                    UpdateIconVisibility();
                    return default;
                }

                if (message == CallbackMessage)
                {
                    var notification = unchecked((uint)lParam.Value);
                    if (notification is PInvoke.WM_LBUTTONUP or PInvoke.WM_LBUTTONDBLCLK or PInvoke.WM_RBUTTONUP)
                        ShowMenu();
                    return default;
                }

                return PInvoke.DefWindowProc(handle, message, wParam, lParam);
            }
            catch (Exception error)
            {
                callbackFailure = error;
                PInvoke.PostQuitMessage(1);
                return default;
            }
        }

        private void ShowMenu()
        {
            using var menu = PInvoke.CreatePopupMenu_SafeHandle();
            if (menu.IsInvalid) throw NativeError("CreatePopupMenu");
            if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, QuitCommand, "Quit HomeDeskSpace"))
                throw NativeError("AppendMenu");
            if (!PInvoke.GetCursorPos(out var point)) throw NativeError("GetCursorPos");
            PInvoke.SetForegroundWindow(window);
            var command = PInvoke.TrackPopupMenu(menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD,
                point.X, point.Y, window);
            if (command.Value == QuitCommand)
            {
                requestShutdown();
                PInvoke.PostQuitMessage(0);
            }

            PInvoke.PostMessage(window, PInvoke.WM_NULL, default, default);
        }

        public void Dispose()
        {
            visibility.VisibilityChanged -= RequestVisibilityRefresh;
            if (window != default)
            {
                RemoveIcon();
                PInvoke.DestroyWindow(window);
            }

            if (atom != 0) PInvoke.UnregisterClass(className, instance!);
            instance?.Dispose();
            GC.KeepAlive(windowProcedure);
        }

        private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation);
    }
}