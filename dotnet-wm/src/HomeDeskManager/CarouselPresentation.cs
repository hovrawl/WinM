using Avalonia;
using HomeDesk.Contracts;
using ContractPixelRect = HomeDesk.Contracts.PixelRect;

namespace HomeDeskManager;

public readonly record struct CarouselCardLayout(Guid WindowId, Rect CardBounds, Rect PreviewBounds)
{
    public Rect PreviewCardBounds => new(PreviewBounds.X - CardBounds.X, PreviewBounds.Y - CardBounds.Y,
        PreviewBounds.Width, PreviewBounds.Height);
}

public readonly record struct CarouselPreviewClip(Rect DestinationBounds, ContractPixelRect SourceBounds);
public readonly record struct PreviewImageSize(int Width, int Height);

public static class CarouselPresentation
{
    public static double MinimizedCardWidth(double workAreaWidth)
    {
        ValidateDimensions(workAreaWidth, 1, 0);
        return Math.Clamp(workAreaWidth / 8, 96, 180);
    }

    public static double MinimizedCardHeight(double workAreaHeight)
    {
        ValidateDimensions(1, workAreaHeight, 0);
        return Math.Clamp(workAreaHeight / 12, 48, 64);
    }

    public static double MinimizedCardStride(double workAreaWidth, double spacing = 12) =>
        MinimizedCardWidth(workAreaWidth) + spacing;

    public static double MaximumMinimizedScrollOffset(int cardCount, double workAreaWidth, double viewportWidth,
        double spacing = 12)
    {
        if (cardCount < 0 || !double.IsFinite(viewportWidth) || viewportWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(cardCount));
        if (cardCount == 0) return 0;
        var cardWidth = MinimizedCardWidth(workAreaWidth);
        var leadingInset = Math.Max(0, (viewportWidth - cardWidth) / 2);
        var contentWidth = (cardCount - 1) * MinimizedCardStride(workAreaWidth, spacing) + cardWidth +
                           leadingInset * 2;
        return Math.Max(0, contentWidth - viewportWidth);
    }

    public static double CardWidth(double workAreaWidth)
    {
        ValidateDimensions(workAreaWidth, 1, 0);
        return workAreaWidth / 2 + 32;
    }

    public static double CardStride(double workAreaWidth, double spacing = 24)
    {
        ValidateDimensions(workAreaWidth, 1, spacing);
        return CardWidth(workAreaWidth) + spacing;
    }

    public static double MaximumScrollOffset(int cardCount, double workAreaWidth, double viewportWidth,
        double spacing = 24)
    {
        if (cardCount < 0 || !double.IsFinite(viewportWidth) || viewportWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(cardCount));
        if (cardCount == 0) return 0;
        var cardWidth = CardWidth(workAreaWidth);
        var leadingInset = Math.Max(0, (viewportWidth - cardWidth) / 2);
        var contentWidth = (cardCount - 1) * CardStride(workAreaWidth, spacing) + cardWidth + leadingInset * 2;
        return Math.Max(0, contentWidth - viewportWidth);
    }

    public static IReadOnlyList<CarouselCardLayout> Layout(IReadOnlyList<WindowInfo> windows,
        IReadOnlyDictionary<Guid, PreviewImageSize> sourceSizes, double workAreaWidth, double workAreaHeight,
        double viewportWidth, double viewportHeight, double horizontalOffset = 0, double spacing = 24,
        Guid? selectedWindowId = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(sourceSizes);
        ValidateDimensions(workAreaWidth, workAreaHeight, spacing);
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) || viewportWidth < 0 ||
            viewportHeight < 0 || !double.IsFinite(horizontalOffset) || horizontalOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));

        var maximumPreviewWidth = Math.Max(1, workAreaWidth / 2);
        var maximumPreviewHeight = Math.Max(1, workAreaHeight / 2);
        var cardWidth = maximumPreviewWidth + 32;
        var cardHeight = maximumPreviewHeight + 88;
        var top = Math.Max(0, (viewportHeight - cardHeight) / 2);
        var leadingInset = Math.Max(0, (viewportWidth - cardWidth) / 2);
        var hasSelection = selectedWindowId is { } selectedId && windows.Any(window => window.Id == selectedId);
        var layouts = new CarouselCardLayout[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var sourceSize = sourceSizes.TryGetValue(window.Id, out var liveSize)
                ? liveSize
                : new PreviewImageSize(window.Bounds.Width, window.Bounds.Height);
            var sourceWidth = Math.Max(1, sourceSize.Width);
            var sourceHeight = Math.Max(1, sourceSize.Height);
            var scale = Math.Min(maximumPreviewWidth / sourceWidth, maximumPreviewHeight / sourceHeight);
            var previewWidth = sourceWidth * scale;
            var previewHeight = sourceHeight * scale;
            var x = leadingInset + index * (cardWidth + spacing) - horizontalOffset;
            var cardCenterX = x + cardWidth / 2;
            var cardCenterY = top + cardHeight / 2;
            var cardScale = hasSelection ? window.Id == selectedWindowId ? 1.08 : 0.96 : 1;
            var scaledCardWidth = cardWidth * cardScale;
            var scaledCardHeight = cardHeight * cardScale;
            var card = new Rect(cardCenterX - scaledCardWidth / 2, cardCenterY - scaledCardHeight / 2,
                scaledCardWidth, scaledCardHeight);
            var previewTop = top + 48 + (maximumPreviewHeight - previewHeight) / 2;
            var previewCenterY = previewTop + previewHeight / 2;
            var scaledPreviewWidth = previewWidth * cardScale;
            var scaledPreviewHeight = previewHeight * cardScale;
            var preview = new Rect(cardCenterX - scaledPreviewWidth / 2,
                previewCenterY - scaledPreviewHeight / 2, scaledPreviewWidth, scaledPreviewHeight);
            layouts[index] = new CarouselCardLayout(window.Id, card, preview);
        }

        return layouts;
    }

    public static IReadOnlyList<CarouselCardLayout> LayoutMinimized(IReadOnlyList<WindowInfo> windows,
        double workAreaWidth, double workAreaHeight, double viewportWidth, double viewportHeight,
        double horizontalOffset = 0, Guid? selectedWindowId = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ValidateDimensions(workAreaWidth, workAreaHeight, 12);
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) || viewportWidth < 0 ||
            viewportHeight < 0 || !double.IsFinite(horizontalOffset) || horizontalOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));

        var cardWidth = MinimizedCardWidth(workAreaWidth);
        var cardHeight = MinimizedCardHeight(workAreaHeight);
        var leadingInset = Math.Max(0, (viewportWidth - cardWidth) / 2);
        var top = Math.Max(0, (viewportHeight - cardHeight) / 2);
        var hasSelection = selectedWindowId is { } selected && windows.Any(window => window.Id == selected);
        var layouts = new CarouselCardLayout[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var x = leadingInset + index * MinimizedCardStride(workAreaWidth) - horizontalOffset;
            var scale = hasSelection ? window.Id == selectedWindowId ? 1.04 : 0.98 : 1;
            var width = cardWidth * scale;
            var height = cardHeight * scale;
            var card = new Rect(x + (cardWidth - width) / 2, top + (cardHeight - height) / 2, width, height);
            layouts[index] = new CarouselCardLayout(window.Id, card, card);
        }

        return layouts;
    }

    public static bool TryClipPreview(Rect preview, Rect viewport, PreviewImageSize sourceSize,
        out CarouselPreviewClip clip)
    {
        clip = default;
        if (preview.Width <= 0 || preview.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0 ||
            sourceSize.Width <= 0 || sourceSize.Height <= 0) return false;
        var left = Math.Max(preview.Left, viewport.Left);
        var top = Math.Max(preview.Top, viewport.Top);
        var right = Math.Min(preview.Right, viewport.Right);
        var bottom = Math.Min(preview.Bottom, viewport.Bottom);
        if (right <= left || bottom <= top) return false;

        var sourceLeft = Math.Clamp((int)Math.Round((left - preview.Left) / preview.Width * sourceSize.Width),
            0, sourceSize.Width - 1);
        var sourceTop = Math.Clamp((int)Math.Round((top - preview.Top) / preview.Height * sourceSize.Height),
            0, sourceSize.Height - 1);
        var sourceRight = Math.Clamp((int)Math.Round((right - preview.Left) / preview.Width * sourceSize.Width),
            sourceLeft + 1, sourceSize.Width);
        var sourceBottom = Math.Clamp((int)Math.Round((bottom - preview.Top) / preview.Height * sourceSize.Height),
            sourceTop + 1, sourceSize.Height);
        clip = new CarouselPreviewClip(new Rect(left, top, right - left, bottom - top),
            new ContractPixelRect(sourceLeft, sourceTop, sourceRight - sourceLeft, sourceBottom - sourceTop));
        return true;
    }

    private static void ValidateDimensions(double width, double height, double spacing)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(spacing) || width <= 0 ||
            height <= 0 || spacing < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
    }
}

internal enum CarouselRailTarget
{
    Main,
    Minimized
}

internal readonly record struct CarouselRailCandidates(
    IReadOnlyList<WindowInfo> Main,
    IReadOnlyList<WindowInfo> Minimized);

internal static class CarouselRailPolicy
{
    public static CarouselRailCandidates SplitCandidates(WorkspaceInfo? workspace,
        IReadOnlyList<WindowInfo> desktopWindows, SplitNodeInfo? tree = null)
    {
        var candidates = SpatialSwitcherPresentation.WindowsForWorkspace(workspace, desktopWindows);
        var main = candidates.Where(window => !window.Minimized).ToArray();
        var minimized = candidates.Where(window => window.Minimized).ToArray();
        if (tree is null) return new(main, minimized);

        var windowsById = main.ToDictionary(window => window.Id);
        var orderedMain = new List<WindowInfo>(main.Length);
        var orderedIds = new HashSet<Guid>();
        AppendTreeOrder(tree, windowsById, orderedMain, orderedIds);
        orderedMain.AddRange(main.Where(window => !orderedIds.Contains(window.Id)));
        return new(orderedMain, minimized);
    }

    public static int RestoreIndexAfterSelection(IReadOnlyList<WindowInfo> mainCandidates, Guid? selectedWindowId)
    {
        if (selectedWindowId is not { } selected) return 0;
        var selectedIndex = -1;
        for (var index = 0; index < mainCandidates.Count; index++)
        {
            if (mainCandidates[index].Id != selected) continue;
            selectedIndex = index;
            break;
        }

        return selectedIndex < 0
            ? 0
            : mainCandidates.Take(selectedIndex + 1).Count(window => window.Mode == WindowMode.Tiling);
    }

    private static void AppendTreeOrder(SplitNodeInfo? node, IReadOnlyDictionary<Guid, WindowInfo> windowsById,
        ICollection<WindowInfo> orderedWindows, ISet<Guid> orderedIds)
    {
        if (node is null) return;
        if (node.WindowId is { } windowId && windowsById.TryGetValue(windowId, out var window) &&
            window.Mode == WindowMode.Tiling && orderedIds.Add(windowId))
            orderedWindows.Add(window);
        AppendTreeOrder(node.First, windowsById, orderedWindows, orderedIds);
        AppendTreeOrder(node.Second, windowsById, orderedWindows, orderedIds);
    }

    public static CarouselRailTarget TargetForPointer(Point pointer, Rect minimizedRailBounds,
        bool minimizedRailVisible) =>
        minimizedRailVisible && minimizedRailBounds.Contains(pointer)
            ? CarouselRailTarget.Minimized
            : CarouselRailTarget.Main;
}