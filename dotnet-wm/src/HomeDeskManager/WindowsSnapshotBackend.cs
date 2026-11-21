using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HomeDesk.Contracts;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomeDeskManager;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class WindowsSnapshotBackend : IDisposable
{
    private const int FrameWaitTimeoutMs = 1000;
    private const int MaximumDimension = 1280;
    private const int FirstIUnknownMethodSlot = 3;
    private const int FramePoolTryGetNextFrameSlot = 7;
    private const int FramePoolCreateCaptureSessionSlot = 10;
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidFramePool = new("24EB6D22-1975-4106-A4AE-7D6F77C42003");
    private static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IidGraphicsCaptureSession = new("814E42A9-F70F-4AD7-939B-FDDCC6EB880D");
    private static readonly Guid IidGraphicsCaptureSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IidGraphicsCaptureFrame = new("FA50C623-38DA-4B32-ACF3-FA9734AD800E");
    private static readonly Guid IidDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidDxgiDevice = typeof(IDXGIDevice).GUID;
    private static readonly Guid IidD3D11Texture2D = typeof(ID3D11Texture2D).GUID;
    private readonly object deviceSync = new();
    private SharedDevice? sharedDevice;
    private bool disposed;

    public Task<OwnedPreviewFrame?> CaptureAsync(WindowPreviewTarget target, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(target, cancellationToken), cancellationToken);

    public void Dispose()
    {
        lock (deviceSync)
        {
            if (disposed) return;
            disposed = true;
            sharedDevice?.Dispose();
            sharedDevice = null;
        }
    }

    private OwnedPreviewFrame? Capture(WindowPreviewTarget target, CancellationToken cancellationToken)
    {
        var deviceSetupMs = 0d;
        var sessionSetupMs = 0d;
        var frameWaitMs = 0d;
        var readbackMs = 0d;
        var resizeMs = 0d;
        PreviewCaptureTimings Timings() => new(deviceSetupMs, sessionSetupMs, frameWaitMs, readbackMs, resizeMs);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidSource(target)) throw new PreviewCaptureException("target-invalid", Timings());

            var setupTimer = Stopwatch.StartNew();
            SharedDevice device;
            try
            {
                device = GetSharedDevice();
            }
            finally
            {
                setupTimer.Stop();
                deviceSetupMs = setupTimer.Elapsed.TotalMilliseconds;
            }

            var item = IntPtr.Zero;
            var pool = IntPtr.Zero;
            var session = IntPtr.Zero;
            try
            {
                var sessionTimer = Stopwatch.StartNew();
                SizeInt32 size;
                try
                {
                    item = CreateCaptureItem((nint)target.HandleValue);
                    var getSize = GetVtMethod<VtGetSizeDelegate>(item, 7);
                    Marshal.ThrowExceptionForHR(getSize(item, out size));
                    if (size.Width <= 0 || size.Height <= 0)
                        throw new PreviewCaptureException("empty-source", Timings());

                    pool = CreateFreeThreadedPool(device.WinRtDevice, size);
                    Marshal.ThrowExceptionForHR(InvokeCreateCaptureSession(pool, item, out session));
                    DisableCursorCapture(session);
                    var startCapture = GetVtMethod<VtStartCaptureDelegate>(session, 6);
                    Marshal.ThrowExceptionForHR(startCapture(session));
                }
                finally
                {
                    sessionTimer.Stop();
                    sessionSetupMs = sessionTimer.Elapsed.TotalMilliseconds;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var frameTimer = Stopwatch.StartNew();
                IntPtr frame = IntPtr.Zero;
                try
                {
                    while (frameTimer.ElapsedMilliseconds < FrameWaitTimeoutMs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = InvokeTryGetNextFrame(pool, out frame);
                        if (result >= 0 && frame != IntPtr.Zero) break;
                        frame = IntPtr.Zero;
                        Thread.Sleep(16);
                    }
                }
                finally
                {
                    frameTimer.Stop();
                    frameWaitMs = frameTimer.Elapsed.TotalMilliseconds;
                }

                if (frame == IntPtr.Zero)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PreviewCaptureException("no-frame-timeout", Timings());
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsValidSource(target)) throw new PreviewCaptureException("target-invalid", Timings());
                    var getSurface = GetVtMethod<VtGetSurfaceDelegate>(frame, 6);
                    Marshal.ThrowExceptionForHR(getSurface(frame, out var surface));
                    try
                    {
                        var readbackTimer = Stopwatch.StartNew();
                        Bitmap captured;
                        try
                        {
                            captured = SurfaceToBitmap(surface, device);
                        }
                        finally
                        {
                            readbackTimer.Stop();
                            readbackMs = readbackTimer.Elapsed.TotalMilliseconds;
                        }

                        using (captured)
                        {
                            var resizeTimer = Stopwatch.StartNew();
                            int width;
                            int height;
                            byte[] pixels;
                            bool sourceIsValid;
                            try
                            {
                                var scale = Math.Min(1d,
                                    MaximumDimension / (double)Math.Max(captured.Width, captured.Height));
                                width = Math.Max(1, (int)Math.Round(captured.Width * scale));
                                height = Math.Max(1, (int)Math.Round(captured.Height * scale));
                                using var resized = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                                if (width == captured.Width && height == captured.Height)
                                {
                                    using var graphics = Graphics.FromImage(resized);
                                    graphics.DrawImageUnscaled(captured, 0, 0);
                                }
                                else
                                {
                                    using var graphics = Graphics.FromImage(resized);
                                    graphics.InterpolationMode =
                                        System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    graphics.DrawImage(captured, 0, 0, width, height);
                                }

                                pixels = CopyBitmapPixels(resized);
                                cancellationToken.ThrowIfCancellationRequested();
                                sourceIsValid = IsValidSource(target);
                            }
                            finally
                            {
                                resizeTimer.Stop();
                                resizeMs = resizeTimer.Elapsed.TotalMilliseconds;
                            }

                            if (!sourceIsValid) throw new PreviewCaptureException("target-invalid", Timings());
                            return new OwnedPreviewFrame(width, height, pixels, Timings());
                        }
                    }
                    finally
                    {
                        ComCloseAndRelease(surface);
                    }
                }
                finally
                {
                    ComCloseAndRelease(frame);
                }
            }
            finally
            {
                ComCloseAndRelease(session);
                ComCloseAndRelease(pool);
                ComRelease(item);
            }
        }
        catch (PreviewCaptureException error)
        {
            throw new PreviewCaptureException(error.FailureCode, Timings(), error.InnerException);
        }
        catch (OperationCanceledException error)
        {
            throw new PreviewCaptureException("cancelled", Timings(), error);
        }
        catch (Exception error)
        {
            throw new PreviewCaptureException("capture-error", Timings(), error);
        }
    }

    private SharedDevice GetSharedDevice()
    {
        lock (deviceSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sharedDevice ??= SharedDevice.Create();
        }
    }

    private static Bitmap SurfaceToBitmap(IntPtr surface, SharedDevice device)
    {
        var accessId = IidDirect3DDxgiInterfaceAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessId, out var access));
        try
        {
            var textureId = IidD3D11Texture2D;
            Marshal.ThrowExceptionForHR(InvokeGetInterface(access, ref textureId, out var texturePointer));
            using var texture = new ID3D11Texture2D(texturePointer);
            var description = texture.Description;
            var stagingDescription = new Texture2DDescription
            {
                Width = description.Width,
                Height = description.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = description.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            lock (device.ContextSync)
            {
                using var staging = device.D3DDevice.CreateTexture2D(stagingDescription);
                device.Context.CopyResource(staging, texture);
                var mapped = device.Context.Map(staging, 0, MapMode.Read);
                try
                {
                    var bitmap = new Bitmap((int)description.Width, (int)description.Height,
                        PixelFormat.Format32bppPArgb);
                    var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                    var bitmapData = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        var rowLength = checked(bitmap.Width * 4);
                        var rowBytes = new byte[rowLength];
                        for (var row = 0; row < bitmap.Height; row++)
                        {
                            var sourceOffset = checked((int)(row * (long)mapped.RowPitch));
                            var source = IntPtr.Add(mapped.DataPointer, sourceOffset);
                            var destinationRow = bitmapData.Stride >= 0 ? row : bitmap.Height - 1 - row;
                            var destination = IntPtr.Add(bitmapData.Scan0,
                                destinationRow * Math.Abs(bitmapData.Stride));
                            Marshal.Copy(source, rowBytes, 0, rowLength);
                            Marshal.Copy(rowBytes, 0, destination, rowLength);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
                    }

                    return bitmap;
                }
                finally
                {
                    device.Context.Unmap(staging, 0);
                }
            }
        }
        finally
        {
            ComRelease(access);
        }
    }

    private static byte[] CopyBitmapPixels(Bitmap bitmap)
    {
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var rowLength = checked(bitmap.Width * 4);
            for (var row = 0; row < bitmap.Height; row++)
            {
                var sourceRow = bitmapData.Stride >= 0 ? row : bitmap.Height - 1 - row;
                var source = IntPtr.Add(bitmapData.Scan0, sourceRow * Math.Abs(bitmapData.Stride));
                Marshal.Copy(source, pixels, row * rowLength, rowLength);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return pixels;
    }

    private static IntPtr CreateCaptureItem(nint window)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classId));
        try
        {
            var interopId = IidGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref interopId, out var factory));
            try
            {
                var itemId = IidGraphicsCaptureItem;
                Marshal.ThrowExceptionForHR(InvokeCreateForWindow(factory, window, ref itemId, out var item));
                return item;
            }
            finally
            {
                ComRelease(factory);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    internal static int InvokeCreateForWindow(IntPtr factory, nint window, ref Guid interfaceId, out IntPtr item)
    {
        var createForWindow = GetVtMethod<VtCreateForWindowDelegate>(factory, FirstIUnknownMethodSlot);
        return createForWindow(factory, window, ref interfaceId, out item);
    }

    private static IntPtr CreateFreeThreadedPool(IntPtr device, SizeInt32 size)
    {
        const string className = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classId));
        try
        {
            var staticsId = IidFramePoolStatics2;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref staticsId, out var factory));
            try
            {
                var create = GetVtMethod<VtCreateFreeThreadedDelegate>(factory, 6);
                Marshal.ThrowExceptionForHR(create(factory, device, 87, 2, size, out var pool));
                return pool;
            }
            finally
            {
                ComRelease(factory);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    internal static int InvokeGetInterface(IntPtr access, ref Guid interfaceId, out IntPtr result)
    {
        var getInterface = GetVtMethod<VtGetInterfaceDelegate>(access, FirstIUnknownMethodSlot);
        return getInterface(access, ref interfaceId, out result);
    }

    internal static int InvokeCreateCaptureSession(IntPtr framePool, IntPtr item, out IntPtr session)
    {
        var createSession = GetVtMethod<VtCreateCaptureSessionDelegate>(framePool,
            FramePoolCreateCaptureSessionSlot);
        return createSession(framePool, item, out session);
    }

    internal static int InvokeTryGetNextFrame(IntPtr framePool, out IntPtr frame)
    {
        var tryGetNextFrame = GetVtMethod<VtTryGetNextFrameDelegate>(framePool, FramePoolTryGetNextFrameSlot);
        return tryGetNextFrame(framePool, out frame);
    }

    private static void DisableCursorCapture(IntPtr session)
    {
        IntPtr sessionInterface = IntPtr.Zero;
        try
        {
            sessionInterface = ComQI(session, IidGraphicsCaptureSession2);
            Marshal.ThrowExceptionForHR(
                GetVtMethod<VtPutBooleanDelegate>(sessionInterface, 7)(sessionInterface, 0));
        }
        finally
        {
            ComRelease(sessionInterface);
        }
    }

    private static IntPtr GetVtableSlot(IntPtr comPointer, int slot)
    {
        var vtable = Marshal.ReadIntPtr(comPointer);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static TDelegate GetVtMethod<TDelegate>(IntPtr comPointer, int slot) where TDelegate : Delegate =>
        Marshal.GetDelegateForFunctionPointer<TDelegate>(GetVtableSlot(comPointer, slot));

    private static IntPtr ComQI(IntPtr comPointer, Guid interfaceId)
    {
        var queryInterface = GetVtMethod<VtQueryInterfaceDelegate>(comPointer, 0);
        Marshal.ThrowExceptionForHR(queryInterface(comPointer, ref interfaceId, out var result));
        return result;
    }

    private static void ComCloseAndRelease(IntPtr comPointer)
    {
        if (comPointer == IntPtr.Zero) return;
        try
        {
            var closable = ComQI(comPointer, IidClosable);
            try
            {
                GetVtMethod<VtCloseDelegate>(closable, 6)(closable);
            }
            finally
            {
                ComRelease(closable);
            }
        }
        catch (Exception error)
        {
            Trace.WriteLine($"Closing a Windows Graphics Capture COM object failed: {error.GetType().Name}.");
        }

        ComRelease(comPointer);
    }

    private static void ComRelease(IntPtr comPointer)
    {
        if (comPointer == IntPtr.Zero) return;
        GetVtMethod<VtReleaseDelegate>(comPointer, 2)(comPointer);
    }

    private static bool IsValidSource(WindowPreviewTarget target)
    {
        var handle = (nint)target.HandleValue;
        if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out var processId) == 0 ||
            processId != target.ProcessId) return false;

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.StartTime.ToUniversalTime().ToFileTimeUtc() == unchecked((long)target.ProcessCreationTime);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or OverflowException)
        {
            return false;
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid interfaceId, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtQueryInterfaceDelegate(IntPtr thisPointer, ref Guid interfaceId, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint VtReleaseDelegate(IntPtr thisPointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtCloseDelegate(IntPtr thisPointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtGetSizeDelegate(IntPtr thisPointer, out SizeInt32 size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int VtCreateForWindowDelegate(IntPtr thisPointer, nint window, ref Guid interfaceId,
        out IntPtr item);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtCreateFreeThreadedDelegate(IntPtr thisPointer, IntPtr device, int pixelFormat,
        int numberOfBuffers, SizeInt32 size, out IntPtr framePool);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int VtCreateCaptureSessionDelegate(IntPtr thisPointer, IntPtr item, out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtStartCaptureDelegate(IntPtr thisPointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int VtTryGetNextFrameDelegate(IntPtr thisPointer, out IntPtr frame);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtGetSurfaceDelegate(IntPtr thisPointer, out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int VtGetInterfaceDelegate(IntPtr thisPointer, ref Guid interfaceId, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VtPutBooleanDelegate(IntPtr thisPointer, byte value);

    private sealed class SharedDevice : IDisposable
    {
        private SharedDevice(ID3D11Device d3DDevice, ID3D11DeviceContext context, IntPtr winRtDevice)
        {
            D3DDevice = d3DDevice;
            Context = context;
            WinRtDevice = winRtDevice;
        }

        public object ContextSync { get; } = new();
        public ID3D11Device D3DDevice { get; }
        public ID3D11DeviceContext Context { get; }
        public IntPtr WinRtDevice { get; }

        public static SharedDevice Create()
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!,
                out var d3DDevice, out var context).CheckError();
            var winRtDevice = IntPtr.Zero;
            try
            {
                using var dxgiDevice = d3DDevice.QueryInterface<IDXGIDevice>();
                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out winRtDevice);
                return new SharedDevice(d3DDevice, context, winRtDevice);
            }
            catch
            {
                ComCloseAndRelease(winRtDevice);
                context.Dispose();
                d3DDevice.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            ComCloseAndRelease(WinRtDevice);
            Context.Dispose();
            D3DDevice.Dispose();
        }
    }
}