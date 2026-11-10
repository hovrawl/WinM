using WinManager.Core;

namespace WinManager.Windows;

internal interface IWindowStackingNative
{
    nint ForegroundWindow { get; }
    bool IsTopmost(nint window);
    bool SetWindowPos(nint window, nint insertAfter, uint flags);
}

internal readonly record struct WindowStackingTarget(nint Handle, NativeActionFailure? Failure);

internal static class WindowStacking
{
    internal const uint PassivePositionFlags = 0x00004213;

    internal static void Apply(IEnumerable<Guid> windowIds, IWindowStackingNative native,
        Func<Guid, bool> shouldSkip, Func<Guid, WindowStackingTarget> resolveTarget,
        Action<NativeActionFailure> reportFailure, Func<Guid, NativeActionFailure> createPositionFailure,
        CancellationToken cancellationToken)
    {
        var foreground = native.ForegroundWindow;
        // A topmost HWND must not be used as insert-after: doing so can promote ordinary windows.
        var insertAfter = foreground != 0 && !native.IsTopmost(foreground) ? foreground : 0;

        foreach (var windowId in windowIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldSkip(windowId)) continue;

            var target = resolveTarget(windowId);
            if (target.Failure is { } failure)
            {
                reportFailure(failure);
                continue;
            }

            if (target.Handle == 0 || target.Handle == foreground) continue;
            if (!native.SetWindowPos(target.Handle, insertAfter, PassivePositionFlags))
            {
                reportFailure(createPositionFailure(windowId));
                continue;
            }

            insertAfter = target.Handle;
        }
    }
}