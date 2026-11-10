using WinManager.Contracts;

namespace WinManager.Core;

public static class LayeredLayout
{
    public static LayoutPlan Layout(MonitorInfo monitor, IReadOnlyList<WindowInfo> orderedWindows, int outerGap,
        LayeredLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(orderedWindows);
        ArgumentOutOfRangeException.ThrowIfNegative(outerGap);
        if (monitor.Dpi == 0) throw new ArgumentOutOfRangeException(nameof(monitor));
        options ??= LayeredLayoutOptions.Default;
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));

        var candidates = orderedWindows.Where(window => window.Mode == WindowMode.Tiling && !window.IsIgnored &&
                                                        (window.LayoutOverflow || window.Visible && !window.Minimized))
            .DistinctBy(window => window.Id).ToArray();
        if (candidates.Length == 0) return new LayoutPlan(Array.Empty<WindowPlacement>());

        var work = monitor.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
            throw new InvalidOperationException("Layered layout requires a positive monitor work area.");

        var overflow = candidates.Where(window => Math.Max(1, window.MinimumWidth) > work.Width ||
                                                  Math.Max(1, window.MinimumHeight) > work.Height).ToArray();
        var fitting = candidates.Except(overflow).ToArray();
        if (fitting.Length == 0)
            return new LayoutPlan(Array.Empty<WindowPlacement>(),
                OverflowWindowIds: Array.AsReadOnly(overflow.Select(window => window.Id).ToArray()));

        var maximumMinimumWidth = fitting.Max(window => Math.Max(1, window.MinimumWidth));
        var maximumMinimumHeight = fitting.Max(window => Math.Max(1, window.MinimumHeight));
        var requestedOuter = GapPixels(outerGap, monitor.Dpi);
        var outer = Math.Min(requestedOuter, Math.Min((work.Width - maximumMinimumWidth) / 2,
            (work.Height - maximumMinimumHeight) / 2));
        var areaWidth = work.Width - 2L * outer;
        var areaHeight = work.Height - 2L * outer;
        var preferredPageWidth = (long)Math.Round(areaWidth * options.WindowWidthRatio,
            MidpointRounding.AwayFromZero);
        var maximumOffset = fitting.Length <= 1
            ? 0
            : Math.Max(0, areaWidth - maximumMinimumWidth) / (fitting.Length - 1);
        var offset = Math.Min(GapPixels(options.HorizontalOffset, monitor.Dpi), maximumOffset);
        var spread = offset * Math.Max(0, fitting.Length - 1);
        var pageWidth = Math.Max(maximumMinimumWidth, Math.Min(preferredPageWidth, areaWidth - spread));
        var placements = new List<WindowPlacement>(fitting.Length);
        for (var index = 0; index < fitting.Length; index++)
        {
            placements.Add(new(fitting[index].Id, Rectangle((long)work.X + outer + offset * index,
                (long)work.Y + outer, pageWidth, areaHeight)));
        }

        return new LayoutPlan(placements.AsReadOnly(),
            OverflowWindowIds: Array.AsReadOnly(overflow.Select(window => window.Id).ToArray()));
    }

    private static int GapPixels(int gap, uint dpi)
    {
        var pixels = Math.Round(gap * (double)dpi / 96, MidpointRounding.AwayFromZero);
        if (pixels > int.MaxValue) throw new InvalidOperationException("DPI-scaled gap exceeds the pixel range.");
        return (int)pixels;
    }

    private static PixelRect Rectangle(long x, long y, long width, long height)
    {
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue ||
            x < int.MinValue || y < int.MinValue || x + width > int.MaxValue || y + height > int.MaxValue)
            throw new InvalidOperationException("Layered layout cannot produce a valid pixel rectangle.");
        return new PixelRect((int)x, (int)y, (int)width, (int)height);
    }
}