using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using HomeDesk.Contracts;

namespace HomeDesk.Windows;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class TouchpadInputSource : IAsyncDisposable
{
    private const uint StopMessage = 0x8006;
    private const uint RawInputMessage = 0x00ff;
    private const uint RawInputDeviceChangeMessage = 0x00fe;
    private const uint RawInput = 0x10000003;
    private const uint RawInputDeviceNotification = 0x00002000;
    private const uint RawInputSink = 0x00000100;
    private const uint RawInputRemove = 0x00000001;
    private const uint RawInputDeviceArrival = 1;
    private const uint RawInputDeviceRemoval = 2;
    private const uint RawInputHidType = 2;
    private const int MaximumRawInputBytes = 1024 * 1024;
    private const int MaximumInputDevices = 128;
    private const uint MaximumReportsPerPacket = 128;
    private readonly object gate = new();
    private readonly object devicesGate = new();
    private readonly object refreshGate = new();
    private readonly ConcurrentDictionary<nint, RawHidTouchpadDevice> devices = new();
    private readonly TouchpadInputQueue<RawInputBatch> input = new(64);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WNDPROC windowProcedure;
    private readonly string windowClass = $"WinManager.Touchpad.{Guid.NewGuid():N}";
    private readonly Dictionary<nint, TouchpadDeviceProcessor> processors = [];
    private readonly TouchpadOptions options;
    private Thread? thread;
    private Task? processor;
    private HWND notificationWindow;
    private TouchpadCapabilityResult capability = new(TouchpadCapabilityState.Disabled, 0, false, false, null);
    private bool disposing;
    private int disposed;

    public TouchpadInputSource(TouchpadOptions? options = null)
    {
        this.options = options ?? new TouchpadOptions();
        windowProcedure = WindowProcedure;
    }

    public TouchpadCapabilityResult Capability => Volatile.Read(ref capability);

    public event Action<TouchpadContactFrame>? FrameReceived;
    public event Action<TouchpadGesture>? GestureRecognized;
    public event Action<TouchpadCapabilityResult>? CapabilityChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (thread is not null) throw new InvalidOperationException("Touchpad input source is already started.");
            thread = new Thread(RunNativeLoop)
            {
                IsBackground = true,
                Name = "WinManager raw touchpad input"
            };
            processor = ProcessInputAsync();
            thread.Start();
        }

        try
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            await stopped.Task.ConfigureAwait(false);
            if (processor is not null) await processor.ConfigureAwait(false);
            return;
        }

        lock (gate)
        {
            disposing = true;
            if (notificationWindow != default)
                PInvoke.PostMessage(notificationWindow, StopMessage, default, default);
            else if (thread is null)
            {
                input.Complete();
                started.TrySetException(new ObjectDisposedException(nameof(TouchpadInputSource)));
                stopped.TrySetResult();
            }
        }

        await stopped.Task.ConfigureAwait(false);
        if (processor is not null) await processor.ConfigureAwait(false);
    }

    private void RunNativeLoop()
    {
        Exception? failure = null;
        ushort atom = 0;
        HWND window = default;
        FreeLibrarySafeHandle? instance = null;
        var previousDpi = default(DPI_AWARENESS_CONTEXT);
        try
        {
            previousDpi = PInvoke.SetThreadDpiAwarenessContext(
                DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previousDpi == default) throw NativeError("SetThreadDpiAwarenessContext");
            instance = PInvoke.GetModuleHandle((string?)null);
            if (instance.IsInvalid) throw NativeError("GetModuleHandle");
            unsafe
            {
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
                    window = PInvoke.CreateWindowEx(0, windowClass, "", 0, 0, 0, 0, 0, default, default, instance,
                        null);
                }
            }

            if (window == default) throw NativeError("CreateWindowEx");
            lock (gate)
            {
                notificationWindow = window;
                if (disposing) throw new ObjectDisposedException(nameof(TouchpadInputSource));
            }

            if (!RegisterTouchpadInput(window))
                SetCapability(TouchpadCapabilityState.RegistrationFailed, 0, false,
                    "RegisterRawInputDevices failed; native gestures are unavailable.");
            else
                RefreshDevices();

            started.TrySetResult();
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
            lock (refreshGate)
            lock (devicesGate)
            {
                foreach (var device in devices.Values) device.Dispose();
                devices.Clear();
                foreach (var processor in processors.Values) processor.Cancel();
                processors.Clear();
            }

            if (window != default)
            {
                RegisterRawInputDevices([
                    new RawInputDevice
                    {
                        UsagePage = 0x0d,
                        Usage = 0x05,
                        Flags = RawInputRemove
                    }
                ], 1, (uint)Marshal.SizeOf<RawInputDevice>());
                PInvoke.DestroyWindow(window);
            }

            if (atom != 0) PInvoke.UnregisterClass(windowClass, instance!);
            instance?.Dispose();
            if (previousDpi != default) PInvoke.SetThreadDpiAwarenessContext(previousDpi);
            lock (gate) notificationWindow = default;
            input.Complete(failure);
            started.TrySetException(failure ?? new ObjectDisposedException(nameof(TouchpadInputSource)));
            stopped.TrySetResult();
            GC.KeepAlive(windowProcedure);
        }
    }

    private unsafe bool RegisterTouchpadInput(HWND window)
    {
        var device = new RawInputDevice
        {
            UsagePage = 0x0d,
            Usage = 0x05,
            Flags = RawInputSink | RawInputDeviceNotification,
            Target = (nint)window.Value
        };
        return RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (message == StopMessage)
            {
                PInvoke.PostQuitMessage(0);
                return default;
            }

            if (message == RawInputMessage)
            {
                if (!TryReadInput((nint)lParam.Value, out var batch))
                {
                    input.MarkInputLost();
                    SetCapability(TouchpadCapabilityState.DecodingFailed, Capability.DeviceCount,
                        Capability.ContactReportsObserved, "Raw input report was malformed or exceeded its bounds.");
                }
                else if (batch is not null) input.TryPublish(batch);
            }
            else if (message == RawInputDeviceChangeMessage)
            {
                if ((uint)wParam.Value == RawInputDeviceArrival || (uint)wParam.Value == RawInputDeviceRemoval)
                {
                    input.MarkInputLost();
                    lock (devicesGate)
                        foreach (var processor in processors.Values)
                            processor.Cancel();
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            RefreshDevices();
                        }
                        catch (Exception error) when (error is ExternalException or Win32Exception or
                                                          InvalidOperationException or ObjectDisposedException)
                        {
                            SetCapability(TouchpadCapabilityState.DecodingFailed, Capability.DeviceCount,
                                Capability.ContactReportsObserved, "Touchpad device refresh failed.");
                        }
                    });
                }
            }
        }
        catch (Exception error) when (error is ExternalException or Win32Exception or InvalidOperationException or
                                          ObjectDisposedException or OverflowException)
        {
            input.MarkInputLost();
            SetCapability(TouchpadCapabilityState.DecodingFailed, Capability.DeviceCount,
                Capability.ContactReportsObserved, "Raw touchpad input processing failed.");
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    private void RefreshDevices()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (refreshGate)
        {
            uint count = 0;
            var deviceSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
            if (GetRawInputDeviceList(null, ref count, deviceSize) == uint.MaxValue || count > MaximumInputDevices)
            {
                SetCapability(TouchpadCapabilityState.DecodingFailed, Capability.DeviceCount,
                    Capability.ContactReportsObserved, "Raw input device enumeration failed.");
                return;
            }

            var rawDevices = new RawInputDeviceList[checked((int)count)];
            if (count > 0 && GetRawInputDeviceList(rawDevices, ref count, deviceSize) == uint.MaxValue)
            {
                SetCapability(TouchpadCapabilityState.DecodingFailed, Capability.DeviceCount,
                    Capability.ContactReportsObserved, "Raw input device enumeration failed.");
                return;
            }

            var hidDevices = rawDevices.Where(device => device.Type == RawInputHidType).ToArray();
            var currentHandles = hidDevices.Select(device => device.Device).ToHashSet();
            lock (devicesGate)
            {
                foreach (var handle in devices.Keys.Where(handle => !currentHandles.Contains(handle)).ToArray())
                    if (devices.TryRemove(handle, out var removedDevice))
                    {
                        removedDevice.Dispose();
                        processors.Remove(handle);
                    }
            }

            var descriptorFailure = false;
            foreach (var rawDevice in hidDevices)
            {
                if (devices.ContainsKey(rawDevice.Device)) continue;
                if (RawHidTouchpadDevice.TryCreate(rawDevice.Device, out var device, out var failure))
                {
                    lock (devicesGate)
                    {
                        if (devices.TryAdd(device!.Handle, device))
                            processors.Add(device.Handle, new TouchpadDeviceProcessor(device.Handle, options));
                        else
                            device.Dispose();
                    }
                }
                else if (failure is "unsupported_descriptor" or "descriptor_decode_failed")
                    descriptorFailure = true;
            }

            int deviceCount;
            lock (devicesGate)
            {
                deviceCount = devices.Count;
            }

            var contactsObserved = Capability.ContactReportsObserved;
            if (deviceCount > 0)
                SetCapability(TouchpadCapabilityState.GestureConflictBlocked, deviceCount, contactsObserved,
                    "Contact capture does not establish suppression of Windows gestures; action dispatch is blocked.");
            else if (descriptorFailure)
                SetCapability(TouchpadCapabilityState.DecodingFailed, 0, contactsObserved,
                    "A Precision Touchpad descriptor was present but could not be decoded.");
            else
                SetCapability(TouchpadCapabilityState.UnsupportedDevice, 0, contactsObserved,
                    "No Precision Touchpad HID collection (Digitizers usage 0x05) was found.");
        }
    }

    private unsafe bool TryReadInput(nint rawInput, out RawInputBatch? batch)
    {
        batch = null;
        if (rawInput == 0) return false;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(rawInput, RawInput, 0, ref size, headerSize) != 0 ||
            size < headerSize || size > MaximumRawInputBytes) return false;
        var bytes = new byte[size];
        uint actualSize = size;
        uint copied;
        fixed (byte* buffer = bytes)
            copied = GetRawInputData(rawInput, RawInput, (nint)buffer, ref actualSize, headerSize);
        if (copied == uint.MaxValue || copied != size || actualSize != size) return false;

        var header = MemoryMarshal.Read<RawInputHeader>(bytes);
        if (header.Size != size) return false;
        if (header.Type != RawInputHidType || !devices.ContainsKey(header.Device)) return true;
        var rawHidOffset = checked((int)headerSize);
        if (bytes.Length < rawHidOffset + 8) return false;
        var reportSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(rawHidOffset, 4));
        var reportCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(rawHidOffset + 4, 4));
        var reportsLength = (ulong)reportSize * reportCount;
        var reportsOffset = (ulong)rawHidOffset + 8;
        if (reportSize is < 2 or > 65536 || reportCount is 0 or > MaximumReportsPerPacket ||
            reportsLength > MaximumRawInputBytes || reportsOffset + reportsLength > (ulong)bytes.Length) return false;

        var reports = new byte[reportCount][];
        for (var index = 0; index < reports.Length; index++)
        {
            reports[index] = bytes.AsSpan(checked((int)(reportsOffset + (ulong)index * reportSize)),
                checked((int)reportSize)).ToArray();
        }

        batch = new RawInputBatch(header.Device, Environment.TickCount64, reports);
        return true;
    }

    private async Task ProcessInputAsync()
    {
        try
        {
            await foreach (var queued in input.ReadAllAsync())
            {
                if (queued.ResetBefore)
                    lock (devicesGate)
                        foreach (var processor in processors.Values)
                            processor.Cancel();

                var batch = queued.Value;

                var frames = new List<TouchpadContactFrame>();
                var gestures = new List<TouchpadGesture>();
                string? failure = null;
                var contactsObserved = Capability.ContactReportsObserved;
                var frameObserved = false;
                int deviceCount;
                lock (devicesGate)
                {
                    if (!devices.TryGetValue(batch.DeviceHandle, out var device) ||
                        !processors.TryGetValue(batch.DeviceHandle, out var processor))
                        continue;
                    deviceCount = devices.Count;
                    foreach (var report in batch.Reports)
                    {
                        if (!device.Decoder.TryDecode(batch.DeviceHandle, batch.TimestampMilliseconds, report,
                                out var decoded))
                        {
                            processor.Cancel();
                            failure = "A touchpad report did not match its HID descriptor.";
                            continue;
                        }

                        if (decoded is null) continue;
                        var result = processor.Process(decoded);
                        if (result.Malformed)
                        {
                            failure = "A touchpad contact frame was malformed.";
                            continue;
                        }

                        if (result.Frame is not { } frame) continue;
                        contactsObserved = true;
                        frameObserved = true;
                        frames.Add(frame);
                        gestures.AddRange(result.Gestures);
                    }
                }

                if (failure is not null)
                    SetCapability(TouchpadCapabilityState.DecodingFailed, deviceCount, contactsObserved, failure);
                else if (frameObserved)
                    SetCapability(TouchpadCapabilityState.GestureConflictBlocked, deviceCount, true,
                        "Contact reports are visible, but shell gesture suppression is unverified.");
                foreach (var frame in frames) Publish(FrameReceived, frame);
                foreach (var gesture in gestures) Publish(GestureRecognized, gesture);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            SetCapability(TouchpadCapabilityState.DecodingFailed, Capability.DeviceCount,
                Capability.ContactReportsObserved, "The touchpad report processor stopped unexpectedly.");
        }
    }

    private void SetCapability(TouchpadCapabilityState state, int deviceCount, bool contactsObserved, string detail)
    {
        var updated = new TouchpadCapabilityResult(state, deviceCount, contactsObserved, false, detail);
        if (Interlocked.Exchange(ref capability, updated) == updated) return;
        ThreadPool.QueueUserWorkItem(_ => Publish(CapabilityChanged, updated));
    }

    private static void Publish<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception)
            {
            }
        }
    }

    private static Exception NativeError(string api) => new Win32Exception(Marshal.GetLastWin32Error(), api);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    private sealed record RawInputBatch(nint DeviceHandle, long TimestampMilliseconds, byte[][] Reports);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? devices, ref uint count,
        uint deviceSize);
}