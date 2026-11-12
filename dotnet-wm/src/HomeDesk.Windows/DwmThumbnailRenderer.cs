using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using HomeDesk.Contracts;

namespace HomeDesk.Windows;

public readonly record struct DwmThumbnailSourceSize(int Width, int Height);

[SupportedOSPlatform("windows7.0")]
public sealed class DwmThumbnailRenderer : IDisposable
{
    private readonly nint destination;
    private readonly IDwmThumbnailNativeApi native;
    private readonly Dictionary<Guid, (WindowPreviewTarget Target, nint Thumbnail)> thumbnails = [];
    private bool disposed;

    public DwmThumbnailRenderer(nint destination) : this(destination, new DwmThumbnailNativeApi())
    {
    }

    internal DwmThumbnailRenderer(nint destination, IDwmThumbnailNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (destination == 0) throw new ArgumentOutOfRangeException(nameof(destination));
        this.destination = destination;
        this.native = native;
    }

    public bool TryBind(WindowPreviewTarget target, out DwmThumbnailSourceSize sourceSize, out string? failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        sourceSize = default;
        failure = null;
        if (target.WindowId == Guid.Empty || target.HandleValue == 0 || target.ProcessId == 0 ||
            target.ProcessCreationTime == 0)
        {
            failure = "invalid_target";
            return false;
        }

        Release(target.WindowId);
        nint thumbnail = 0;
        try
        {
            if (!native.IsValidSource(target))
            {
                failure = "stale_window";
                return false;
            }

            if (!native.TryRegister(destination, (nint)target.HandleValue, out thumbnail) || thumbnail == 0)
            {
                if (thumbnail != 0) native.Unregister(thumbnail);
                failure = "registration_failed";
                return false;
            }

            if (!native.TryQuerySourceSize(thumbnail, out sourceSize) || sourceSize.Width <= 0 ||
                sourceSize.Height <= 0)
            {
                var failedThumbnail = thumbnail;
                thumbnail = 0;
                native.Unregister(failedThumbnail);
                sourceSize = default;
                failure = "source_size_unavailable";
                return false;
            }

            thumbnails.Add(target.WindowId, (target, thumbnail));
            thumbnail = 0;
            return true;
        }
        catch (Exception error) when (error is ExternalException or DllNotFoundException or EntryPointNotFoundException
                                          or BadImageFormatException or InvalidOperationException or OverflowException)
        {
            if (thumbnail != 0)
            {
                try
                {
                    native.Unregister(thumbnail);
                }
                catch (Exception)
                {
                }
            }

            sourceSize = default;
            failure = "preview_unavailable";
            return false;
        }
    }

    public bool TryUpdate(Guid windowId, PixelRect destinationRectangle, byte opacity = byte.MaxValue,
        bool visible = true, PixelRect? sourceRectangle = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (destinationRectangle.Width <= 0 || destinationRectangle.Height <= 0 ||
            sourceRectangle is { Width: <= 0 } or { Height: <= 0 } ||
            !thumbnails.TryGetValue(windowId, out var binding)) return false;
        try
        {
            return native.TryUpdate(binding.Thumbnail, destinationRectangle, opacity, visible, sourceRectangle);
        }
        catch (Exception error) when (error is ExternalException or DllNotFoundException or EntryPointNotFoundException
                                          or BadImageFormatException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    public void Release(Guid windowId)
    {
        if (!thumbnails.Remove(windowId, out var binding)) return;
        native.Unregister(binding.Thumbnail);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var binding in thumbnails.Values)
            native.Unregister(binding.Thumbnail);
        thumbnails.Clear();
    }
}

internal interface IDwmThumbnailNativeApi
{
    bool IsValidSource(WindowPreviewTarget target);
    bool TryRegister(nint destination, nint source, out nint thumbnail);
    bool TryQuerySourceSize(nint thumbnail, out DwmThumbnailSourceSize size);
    bool TryUpdate(nint thumbnail, PixelRect destination, byte opacity, bool visible, PixelRect? source);
    void Unregister(nint thumbnail);
}

[SupportedOSPlatform("windows7.0")]
internal sealed class DwmThumbnailNativeApi : IDwmThumbnailNativeApi
{
    private const uint SetDestinationRectangle = 0x00000001;
    private const uint SetOpacity = 0x00000004;
    private const uint SetVisible = 0x00000008;
    private const uint SetSourceRectangle = 0x00000010;

    public bool IsValidSource(WindowPreviewTarget target)
    {
        var window = new HWND((nint)target.HandleValue);
        if (!PInvoke.IsWindow(window)) return false;
        PInvoke.GetWindowThreadProcessId(window, out var processId);
        if (processId != target.ProcessId) return false;
        using var process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid || !PInvoke.GetProcessTimes(process, out var created, out var exited, out _, out _) ||
            exited.dwLowDateTime != 0 || exited.dwHighDateTime != 0) return false;
        var creationTime = ((ulong)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return creationTime == target.ProcessCreationTime;
    }

    public bool TryRegister(nint destination, nint source, out nint thumbnail) =>
        DwmRegisterThumbnail(destination, source, out thumbnail) >= 0 && thumbnail != 0;

    public bool TryQuerySourceSize(nint thumbnail, out DwmThumbnailSourceSize size)
    {
        var result = DwmQueryThumbnailSourceSize(thumbnail, out var nativeSize);
        size = new DwmThumbnailSourceSize(nativeSize.Width, nativeSize.Height);
        return result >= 0;
    }

    public bool TryUpdate(nint thumbnail, PixelRect destination, byte opacity, bool visible, PixelRect? source)
    {
        var properties = new DwmThumbnailProperties
        {
            Flags = SetDestinationRectangle | SetOpacity | SetVisible |
                    (source is null ? 0u : SetSourceRectangle),
            Destination = new NativeRect
            {
                Left = destination.X,
                Top = destination.Y,
                Right = checked(destination.X + destination.Width),
                Bottom = checked(destination.Y + destination.Height)
            },
            Source = source is { } sourceRectangle
                ? new NativeRect
                {
                    Left = sourceRectangle.X,
                    Top = sourceRectangle.Y,
                    Right = checked(sourceRectangle.X + sourceRectangle.Width),
                    Bottom = checked(sourceRectangle.Y + sourceRectangle.Height)
                }
                : default,
            Opacity = opacity,
            Visible = visible
        };
        return DwmUpdateThumbnailProperties(thumbnail, ref properties) >= 0;
    }

    public void Unregister(nint thumbnail) => DwmUnregisterThumbnail(thumbnail);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);
}