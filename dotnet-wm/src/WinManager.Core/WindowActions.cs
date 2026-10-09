using WinManager.Contracts;

namespace WinManager.Core;

public sealed record WindowPlacement(Guid WindowId, PixelRect Bounds);
public sealed record WindowVisibility(Guid WindowId, bool Visible);
public sealed record LayoutPlan(IReadOnlyList<WindowPlacement> Placements, Guid? FocusWindowId = null,
    IReadOnlyList<WindowVisibility>? Visibility = null);
public sealed record NativeActionFailure(Guid? WindowId, string Code, string Message);
public sealed record ApplyResult(IReadOnlyList<NativeActionFailure> Failures);
public interface IWindowPlatform : IDesktopObserver
{
    Task<ApplyResult> ApplyAsync(LayoutPlan plan, CancellationToken cancellationToken);
    Task<ApplyResult> RestoreAsync(CancellationToken cancellationToken);
}