using HomeDesk.Contracts;

namespace HomeDesk.Core;

public static class SpatialNavigation
{
    public static WindowInfo? ClosestWindowInDirection(PixelRect origin, Direction direction,
        IEnumerable<WindowInfo> candidates, Guid? excludeWindowId = null)
    {
        var (originX, originY) = Center(origin);
        return candidates
            .Where(window => window.Id != excludeWindowId && IsEligible(window) &&
                             IsInDirection(originX, originY, Center(window.Bounds), direction))
            .OrderBy(window => DistanceSquared(originX, originY, Center(window.Bounds)))
            .ThenBy(window => window.Id)
            .FirstOrDefault();
    }

    public static WindowInfo? ClosestWindow(PixelRect origin, IEnumerable<WindowInfo> candidates)
    {
        var (originX, originY) = Center(origin);
        return candidates.Where(IsEligible)
            .OrderBy(window => DistanceSquared(originX, originY, Center(window.Bounds)))
            .ThenBy(window => window.Id)
            .FirstOrDefault();
    }

    public static MonitorInfo? ClosestMonitorInDirection(PixelRect origin, Direction direction,
        IEnumerable<MonitorInfo> candidates)
    {
        var (originX, originY) = Center(origin);
        return candidates.Where(monitor => IsInDirection(originX, originY, Center(monitor.Bounds), direction))
            .OrderBy(monitor => DistanceSquared(originX, originY, Center(monitor.Bounds)))
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(monitor => monitor.Id)
            .FirstOrDefault();
    }

    public static PixelRect PlaceAt(WindowInfo window, PixelRect workArea, int x, int y) =>
        Place(window, workArea, x, y);

    public static PixelRect CenterIn(WindowInfo window, PixelRect workArea)
    {
        var (width, height) = SizeIn(window, workArea);
        return Place(window, workArea, workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2);
    }

    public static bool IsEligible(WindowInfo window) =>
        window.Visible && !window.Minimized && !window.LayoutOverflow && !window.IsIgnored &&
        window.Bounds.Width > 0 && window.Bounds.Height > 0;

    private static PixelRect Place(WindowInfo window, PixelRect workArea, long x, long y)
    {
        var (width, height) = SizeIn(window, workArea);
        var maxX = (long)workArea.X + workArea.Width - width;
        var maxY = (long)workArea.Y + workArea.Height - height;
        return new(checked((int)Math.Clamp(x, workArea.X, maxX)),
            checked((int)Math.Clamp(y, workArea.Y, maxY)), width, height);
    }

    private static (int Width, int Height) SizeIn(WindowInfo window, PixelRect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || window.MinimumWidth > workArea.Width ||
            window.MinimumHeight > workArea.Height)
            throw new InvalidOperationException("Window minimum size exceeds the destination work area.");
        return (Math.Min(Math.Max(window.Bounds.Width, window.MinimumWidth), workArea.Width),
            Math.Min(Math.Max(window.Bounds.Height, window.MinimumHeight), workArea.Height));
    }

    private static bool IsInDirection(double originX, double originY, (double X, double Y) target, Direction direction) =>
        direction switch
        {
            Direction.Left => target.X < originX,
            Direction.Right => target.X > originX,
            Direction.Up => target.Y < originY,
            Direction.Down => target.Y > originY,
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

    private static double DistanceSquared(double x, double y, (double X, double Y) point)
    {
        var dx = point.X - x;
        var dy = point.Y - y;
        return dx * dx + dy * dy;
    }

    private static (double X, double Y) Center(PixelRect bounds) =>
        (bounds.X + bounds.Width / 2d, bounds.Y + bounds.Height / 2d);
}
