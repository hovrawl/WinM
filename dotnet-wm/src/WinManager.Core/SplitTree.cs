using WinManager.Contracts;

namespace WinManager.Core;

public sealed class SplitTree
{
    private SplitNodeInfo? root;
    private Dictionary<Guid, WindowInfo> windows = [];
    private readonly HashSet<Guid> resizedSplits = [];
    private SplitOrientation nextOrientation = SplitOrientation.Horizontal;

    public SplitNodeInfo? Snapshot => root;

    public void Reconcile(IReadOnlyList<WindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var eligible = windows.Where(w => Eligible(w) && w.Mode == WindowMode.Tiling)
            .ToDictionary(w => w.Id);
        root = Prune(root, eligible);
        resizedSplits.IntersectWith(BranchIds(root));
        var retained = Leaves(root).Select(n => n.WindowId!.Value).ToHashSet();
        foreach (var id in eligible.Keys.Order())
        {
            if (retained.Contains(id)) continue;
            var leaf = new SplitNodeInfo(Guid.NewGuid(), id, null, .5, null, null);
            if (root is null) root = leaf;
            else
            {
                root = Append(root, leaf, nextOrientation);
                nextOrientation = SplitOrientation.Horizontal;
            }
        }
        root = Balance(root);
        this.windows = eligible;
    }

    public void SetOrientation(Guid windowId, SplitOrientation orientation)
    {
        if (orientation is not (SplitOrientation.Horizontal or SplitOrientation.Vertical))
            throw new ArgumentOutOfRangeException(nameof(orientation));
        var path = new List<SplitNodeInfo>();
        if (!FindPath(root, windowId, path)) return;
        if (path.Count == 0) nextOrientation = orientation;
        else
        {
            var parent = path[^1];
            root = Replace(root!, parent.Id, parent with { Orientation = orientation });
            root = Balance(root);
        }
    }

    public bool Swap(Guid a, Guid b)
    {
        if (a == b || !windows.ContainsKey(a) || !windows.ContainsKey(b)) return false;
        root = SwapLeaves(root!, a, b);
        return true;
    }

    public bool Resize(Guid windowId, Direction direction, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var axis = Axis(direction);
        var path = new List<SplitNodeInfo>();
        if (!FindPath(root, windowId, path)) return false;
        var parent = path.LastOrDefault(n => n.Orientation == axis);
        if (parent is null) return false;
        var delta = direction is Direction.Left or Direction.Up ? -amount : amount;
        var ratio = Math.Clamp(parent.Ratio + delta, .1, .9);
        if (ratio == parent.Ratio) return false;
        resizedSplits.Add(parent.Id);
        root = Replace(root!, parent.Id, parent with { Ratio = ratio });
        return true;
    }

    public bool ResizeToBounds(Guid windowId, PixelRect bounds, MonitorInfo monitor, int innerGap, int outerGap)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        var path = new List<SplitNodeInfo>();
        if (!FindPath(root, windowId, path)) return false;
        var changed = false;
        foreach (var direction in new[] { Direction.Left, Direction.Right, Direction.Up, Direction.Down })
        {
            var horizontal = Axis(direction) == SplitOrientation.Horizontal;
            var firstSide = direction is Direction.Right or Direction.Down;
            var parent = path.LastOrDefault(node => node.Orientation == Axis(direction) &&
                Leaves(firstSide ? node.First : node.Second).Any(leaf => leaf.WindowId == windowId));
            if (parent is null) continue;
            var placements = Layout(monitor, innerGap, outerGap).Placements.ToDictionary(placement => placement.WindowId);
            var current = placements[windowId].Bounds;
            var edge = direction switch
            {
                Direction.Left => (long)bounds.X,
                Direction.Right => (long)bounds.X + bounds.Width,
                Direction.Up => (long)bounds.Y,
                _ => (long)bounds.Y + bounds.Height
            };
            var currentEdge = direction switch
            {
                Direction.Left => (long)current.X,
                Direction.Right => (long)current.X + current.Width,
                Direction.Up => (long)current.Y,
                _ => (long)current.Y + current.Height
            };
            if (edge == currentEdge) continue;
            var tiles = Leaves(parent).Select(leaf => placements[leaf.WindowId!.Value].Bounds).ToArray();
            var start = tiles.Min(tile => horizontal ? (long)tile.X : tile.Y);
            var end = tiles.Max(tile => horizontal ? (long)tile.X + tile.Width : (long)tile.Y + tile.Height);
            var gap = GapPixels(innerGap, monitor.Dpi);
            var available = end - start - gap;
            var size = edge - start - (firstSide ? 0 : gap);
            var ratio = size / (double)available;
            if (Math.Floor(available * ratio) < size) ratio = Math.BitIncrement(ratio);
            ratio = Math.Clamp(ratio, .1, .9);
            if (ratio == parent.Ratio) continue;
            resizedSplits.Add(parent.Id);
            root = Replace(root!, parent.Id, parent with { Ratio = ratio });
            changed = true;
            path.Clear();
            FindPath(root, windowId, path);
        }
        return changed;
    }

    public LayoutPlan Layout(MonitorInfo monitor, int innerGap, int outerGap)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentOutOfRangeException.ThrowIfNegative(innerGap);
        ArgumentOutOfRangeException.ThrowIfNegative(outerGap);
        if (monitor.Dpi == 0) throw new ArgumentOutOfRangeException(nameof(monitor));
        var inner = GapPixels(innerGap, monitor.Dpi);
        var outer = GapPixels(outerGap, monitor.Dpi);
        if (root is null) return new LayoutPlan(Array.Empty<WindowPlacement>());
        var work = monitor.WorkArea;
        var area = Rectangle((long)work.X + outer, (long)work.Y + outer,
            (long)work.Width - 2L * outer, (long)work.Height - 2L * outer);
        var minimums = new Dictionary<Guid, (long Width, long Height)>();
        var minimum = Measure(root, inner, minimums);
        if (area.Width < minimum.Width || area.Height < minimum.Height)
            throw new InvalidOperationException("Work area cannot satisfy one-pixel tiles and configured gaps.");
        var placements = new List<WindowPlacement>();
        Place(root, area, inner, minimums, placements);
        return new LayoutPlan(placements.AsReadOnly());
    }

    public Guid? Neighbor(Guid windowId, Direction direction, IReadOnlyList<WindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        Axis(direction);
        var source = windows.FirstOrDefault(w => w.Id == windowId && Eligible(w) && HasArea(w));
        if (source is null) return null;
        var x = source.Bounds.X + source.Bounds.Width / 2.0;
        var y = source.Bounds.Y + source.Bounds.Height / 2.0;
        Guid? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var candidate in windows)
        {
            if (candidate.Id == windowId || !Eligible(candidate) || !HasArea(candidate)) continue;
            var dx = candidate.Bounds.X + candidate.Bounds.Width / 2.0 - x;
            var dy = candidate.Bounds.Y + candidate.Bounds.Height / 2.0 - y;
            var inDirection = direction switch
            {
                Direction.Left => dx < 0,
                Direction.Right => dx > 0,
                Direction.Up => dy < 0,
                Direction.Down => dy > 0,
                _ => false
            };
            if (!inDirection) continue;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance || (distance == bestDistance &&
                (best is null || candidate.Id.CompareTo(best.Value) < 0)))
            {
                best = candidate.Id;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static bool Eligible(WindowInfo window) => window.Visible && !window.Minimized && !window.IsIgnored;
    private static bool HasArea(WindowInfo window) => window.Bounds.Width > 0 && window.Bounds.Height > 0;
    private static SplitOrientation Axis(Direction direction) => direction switch
    {
        Direction.Left or Direction.Right => SplitOrientation.Horizontal,
        Direction.Up or Direction.Down => SplitOrientation.Vertical,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    private static IEnumerable<SplitNodeInfo> Leaves(SplitNodeInfo? node)
    {
        if (node is null) yield break;
        else if (node.WindowId is not null) yield return node;
        else
        {
            foreach (var leaf in Leaves(node.First)) yield return leaf;
            foreach (var leaf in Leaves(node.Second)) yield return leaf;
        }
    }

    private static IEnumerable<Guid> BranchIds(SplitNodeInfo? node)
    {
        if (node is null || node.WindowId is not null) yield break;
        yield return node.Id;
        foreach (var id in BranchIds(node.First)) yield return id;
        foreach (var id in BranchIds(node.Second)) yield return id;
    }

    private static SplitNodeInfo? Prune(SplitNodeInfo? node, Dictionary<Guid, WindowInfo> eligible)
    {
        if (node is null) return null;
        if (node.WindowId is { } id) return eligible.ContainsKey(id) ? node : null;
        var first = Prune(node.First, eligible);
        var second = Prune(node.Second, eligible);
        if (first is null) return second;
        if (second is null) return first;
        return node with { First = first, Second = second };
    }

    private static SplitNodeInfo Append(SplitNodeInfo node, SplitNodeInfo leaf, SplitOrientation orientation) =>
        node.WindowId is not null
            ? new SplitNodeInfo(Guid.NewGuid(), null, orientation, .5, node, leaf)
            : node with { Second = Append(node.Second!, leaf, orientation) };

    private SplitNodeInfo? Balance(SplitNodeInfo? node)
    {
        if (node is null || node.WindowId is not null) return node;
        var first = Balance(node.First)!;
        var second = Balance(node.Second)!;
        var ratio = node.Ratio;
        if (!resizedSplits.Contains(node.Id))
        {
            var firstSpan = Span(first, node.Orientation!.Value);
            ratio = firstSpan / (double)(firstSpan + Span(second, node.Orientation.Value));
        }
        if (first == node.First && second == node.Second && ratio == node.Ratio) return node;
        return node with { First = first, Second = second, Ratio = ratio };
    }

    private static long Span(SplitNodeInfo node, SplitOrientation orientation)
    {
        if (node.WindowId is not null) return 1;
        var first = Span(node.First!, orientation);
        var second = Span(node.Second!, orientation);
        return node.Orientation == orientation ? first + second : Math.Max(first, second);
    }

    private static bool FindPath(SplitNodeInfo? node, Guid windowId, List<SplitNodeInfo> path)
    {
        if (node is null) return false;
        if (node.WindowId is { } id) return id == windowId;
        path.Add(node);
        if (FindPath(node.First, windowId, path) || FindPath(node.Second, windowId, path)) return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static SplitNodeInfo Replace(SplitNodeInfo node, Guid id, SplitNodeInfo replacement)
    {
        if (node.Id == id) return replacement;
        if (node.WindowId is not null) return node;
        return node with
        {
            First = Replace(node.First!, id, replacement),
            Second = Replace(node.Second!, id, replacement)
        };
    }

    private static SplitNodeInfo SwapLeaves(SplitNodeInfo node, Guid a, Guid b)
    {
        if (node.WindowId is { } id)
            return id == a ? node with { WindowId = b } : id == b ? node with { WindowId = a } : node;
        return node with { First = SwapLeaves(node.First!, a, b), Second = SwapLeaves(node.Second!, a, b) };
    }

    private static int GapPixels(int dip, uint dpi)
    {
        var pixels = ((long)dip * dpi + 48) / 96;
        if (pixels > int.MaxValue) throw new InvalidOperationException("DPI-scaled gap exceeds the pixel range.");
        return (int)pixels;
    }

    private static (long Width, long Height) Measure(SplitNodeInfo node, int gap,
        Dictionary<Guid, (long Width, long Height)> minimums)
    {
        (long Width, long Height) result;
        if (node.WindowId is not null) result = (1, 1);
        else
        {
            var first = Measure(node.First!, gap, minimums);
            var second = Measure(node.Second!, gap, minimums);
            result = node.Orientation == SplitOrientation.Horizontal
                ? (first.Width + gap + second.Width, Math.Max(first.Height, second.Height))
                : (Math.Max(first.Width, second.Width), first.Height + gap + second.Height);
        }
        minimums.Add(node.Id, result);
        return result;
    }

    private void Place(SplitNodeInfo node, PixelRect area, int gap,
        Dictionary<Guid, (long Width, long Height)> minimums, List<WindowPlacement> placements)
    {
        if (node.WindowId is { } id)
        {
            placements.Add(new WindowPlacement(id, area));
            return;
        }
        var horizontal = node.Orientation == SplitOrientation.Horizontal;
        var available = (long)(horizontal ? area.Width : area.Height) - gap;
        var firstMinimum = minimums[node.First!.Id];
        var secondMinimum = minimums[node.Second!.Id];
        var lower = horizontal ? firstMinimum.Width : firstMinimum.Height;
        var upper = available - (horizontal ? secondMinimum.Width : secondMinimum.Height);
        var preferred = resizedSplits.Contains(node.Id)
            ? available * node.Ratio
            : lower + (upper - lower) * node.Ratio;
        var firstSize = Math.Clamp((long)Math.Floor(preferred), lower, upper);
        var secondSize = available - firstSize;
        var first = horizontal
            ? Rectangle(area.X, area.Y, firstSize, area.Height)
            : Rectangle(area.X, area.Y, area.Width, firstSize);
        var second = horizontal
            ? Rectangle((long)area.X + firstSize + gap, area.Y, secondSize, area.Height)
            : Rectangle(area.X, (long)area.Y + firstSize + gap, area.Width, secondSize);
        Place(node.First, first, gap, minimums, placements);
        Place(node.Second, second, gap, minimums, placements);
    }

    private static PixelRect Rectangle(long x, long y, long width, long height)
    {
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue ||
            x < int.MinValue || y < int.MinValue || x + width > int.MaxValue || y + height > int.MaxValue)
            throw new InvalidOperationException("Layout cannot produce a valid pixel rectangle.");
        return new PixelRect((int)x, (int)y, (int)width, (int)height);
    }
}