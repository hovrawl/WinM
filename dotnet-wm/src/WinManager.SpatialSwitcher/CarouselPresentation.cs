using Avalonia;
using WinManager.Contracts;
using WinManager.Windows;
using ContractPixelRect = WinManager.Contracts.PixelRect;

namespace WinManager.SpatialSwitcher;

public readonly record struct CarouselCardLayout(Guid WindowId, Rect CardBounds, Rect PreviewBounds);

public readonly record struct CarouselPreviewClip(Rect DestinationBounds, ContractPixelRect SourceBounds);

public static class CarouselPresentation
{
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
        var contentWidth = (cardCount - 1) * CardStride(workAreaWidth, spacing) + CardWidth(workAreaWidth);
        return Math.Max(0, contentWidth - viewportWidth);
    }

    public static IReadOnlyList<CarouselCardLayout> Layout(IReadOnlyList<WindowInfo> windows,
        IReadOnlyDictionary<Guid, DwmThumbnailSourceSize> sourceSizes, double workAreaWidth, double workAreaHeight,
        double viewportWidth, double viewportHeight, double horizontalOffset = 0, double spacing = 24)
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
        var layouts = new CarouselCardLayout[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var sourceSize = sourceSizes.TryGetValue(window.Id, out var liveSize)
                ? liveSize
                : new DwmThumbnailSourceSize(window.Bounds.Width, window.Bounds.Height);
            var sourceWidth = Math.Max(1, sourceSize.Width);
            var sourceHeight = Math.Max(1, sourceSize.Height);
            var scale = Math.Min(maximumPreviewWidth / sourceWidth, maximumPreviewHeight / sourceHeight);
            var previewWidth = sourceWidth * scale;
            var previewHeight = sourceHeight * scale;
            var x = index * (cardWidth + spacing) - horizontalOffset;
            var card = new Rect(x, top, cardWidth, cardHeight);
            var preview = new Rect(x + (cardWidth - previewWidth) / 2,
                top + 48 + (maximumPreviewHeight - previewHeight) / 2, previewWidth, previewHeight);
            layouts[index] = new CarouselCardLayout(window.Id, card, preview);
        }

        return layouts;
    }

    public static bool TryClipPreview(Rect preview, Rect viewport, DwmThumbnailSourceSize sourceSize,
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