using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinManager.SpatialSwitcher;

internal sealed class SwitcherKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const ushort VkNoname = 0xFC;

    private readonly Action<SwitcherKeyboardAction> onAction;
    private readonly LowLevelKeyboardProc callback;
    private readonly SwitcherKeyboardPolicy policy = new();
    private nint hook;
    private bool disposed;

    public SwitcherKeyboardHook(Action<SwitcherKeyboardAction> onAction)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The keyboard hook requires Windows.");
        this.onAction = onAction;
        callback = HookCallback;
        hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hook != 0) UnhookWindowsHookEx(hook);
        hook = 0;
        GC.KeepAlive(callback);
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (disposed || code < 0) return CallNextHookEx(hook, code, wParam, lParam);

        var decision = default(SwitcherKeyboardDecision);
        try
        {
            var message = unchecked((int)wParam);
            var keyDown = message is WmKeyDown or WmSysKeyDown;
            if (!keyDown && message is not (WmKeyUp or WmSysKeyUp))
                return CallNextHookEx(hook, code, wParam, lParam);

            var key = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            decision = policy.Process(code, unchecked((int)key.VirtualKey), keyDown,
                IsDown(VkControl), IsDown(VkLwin) || IsDown(VkRwin), IsDown(VkShift), IsDown(VkMenu),
                (key.Flags & LlkhfInjected) != 0, enabled: true);
            if (!decision.Suppress) return CallNextHookEx(hook, code, wParam, lParam);

            if (decision.Action is { } action) onAction(action);
            if (decision.Action is not null) SendNeutralKey();
            return 1;
        }
        catch
        {
            return decision.Suppress ? 1 : CallNextHookEx(hook, code, wParam, lParam);
        }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static void SendNeutralKey()
    {
        var inputs = new[]
        {
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new() { VirtualKey = VkNoname } } },
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new() { VirtualKey = VkNoname, Flags = 2 } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int inputSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}