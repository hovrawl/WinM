using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using PInvoke = global::Windows.Win32.TestPInvoke;

namespace WinManager.Windows.Tests;

internal sealed class OwnProcessWindow : IAsyncDisposable
{
    private readonly TaskCompletionSource<uint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint threadId;

    public static async Task<OwnProcessWindow> CreateAsync(string title)
    {
        var fixture = new OwnProcessWindow();
        var thread = new Thread(() => fixture.Run(title)) { IsBackground = true, Name = "Own-process native test window" };
        thread.Start();
        fixture.threadId = await fixture.ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return fixture;
    }

    private unsafe void Run(string title)
    {
        HWND window = default;
        try
        {
            using var module = PInvoke.GetModuleHandle((string?)null);
            window = PInvoke.CreateWindowEx(0, "STATIC", title, WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                100, 100, 400, 300, default, default, module, null);
            if (window == default) throw new Win32Exception(Marshal.GetLastWin32Error());
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            ready.TrySetResult(PInvoke.GetCurrentThreadId());
            while (true)
            {
                var result = PInvoke.GetMessage(out var message, default, 0, 0);
                if (result.Value == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result.Value == 0) break;
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
        }
        finally
        {
            if (window != default) PInvoke.DestroyWindow(window);
            stopped.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!stopped.Task.IsCompleted && !PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}