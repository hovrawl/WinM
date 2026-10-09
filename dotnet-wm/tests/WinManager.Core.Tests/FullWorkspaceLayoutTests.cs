using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class FullWorkspaceLayoutTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(10)]
    public void ColumnsTileEveryWindowAtEqualWidthsDespiteMinimumHints(int count)
    {
        var monitor = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 1920, 1200), new(0, 0, 1920, 1200), 144);
        var windows = Enumerable.Range(1, count).Select(i => new WindowInfo(new(i, 0, 0, new byte[8]),
            monitor.Id, "window", "test", "test", new(0, 0, 800, 1000), true, false, false,
            WindowMode.Tiling, 1000, 1500)).ToArray();
        var tree = new SplitTree();
        tree.Reconcile(windows);
        var plan = tree.Layout(monitor, 8, 8);
        Assert.Equal(count, plan.Placements.Count);
        var widths = plan.Placements.Select(p => p.Bounds.Width).ToArray();
        Assert.InRange(widths.Max() - widths.Min(), 0, 1);
        Assert.Equal(1920 - 24 - 12 * (count - 1), widths.Sum());
        Assert.Equal(12, plan.Placements[0].Bounds.X);
        Assert.All(plan.Placements, p => { Assert.Equal(12, p.Bounds.Y); Assert.Equal(1176, p.Bounds.Height); });
        for (var i = 1; i < count; i++)
            Assert.Equal(plan.Placements[i - 1].Bounds.X + widths[i - 1] + 12, plan.Placements[i].Bounds.X);
        Assert.Equal(1908, plan.Placements[^1].Bounds.X + widths[^1]);
        tree.Reconcile(windows.Reverse().ToArray());
        Assert.Equal(plan.Placements, tree.Layout(monitor, 8, 8).Placements);
    }

    [Fact]
    public void RowsTileEveryWindowAtEqualHeightsAndReflowWhenOneCloses()
    {
        var monitor = new MonitorInfo(Guid.NewGuid(), "display", new(-900, -600, 900, 600), new(-900, -600, 900, 600), 96);
        var windows = Enumerable.Range(1, 4).Select(i => new WindowInfo(new(i, 0, 0, new byte[8]),
            monitor.Id, "window", "test", "test", new(0, 0, 800, 1000), true, false, false,
            WindowMode.Tiling, 10000, 10000)).ToArray();
        var tree = new SplitTree();
        tree.Reconcile(windows);
        foreach (var window in windows) tree.SetOrientation(window.Id, SplitOrientation.Vertical);
        var plan = tree.Layout(monitor, 8, 8);
        Assert.Equal(4, plan.Placements.Count);
        Assert.All(plan.Placements, p => Assert.Equal(new PixelRect(-892, p.Bounds.Y, 884, 140), p.Bounds));
        Assert.Equal(new[] { -592, -444, -296, -148 }, plan.Placements.Select(p => p.Bounds.Y));
        tree.Reconcile(windows.Take(3).ToArray());
        plan = tree.Layout(monitor, 8, 8);
        Assert.Equal(3, plan.Placements.Count);
        Assert.InRange(plan.Placements.Max(p => p.Bounds.Height) - plan.Placements.Min(p => p.Bounds.Height), 0, 1);
        Assert.Equal(568, plan.Placements.Sum(p => p.Bounds.Height));
    }

    [Fact]
    public void NewColumnsRebalanceOnlyUntouchedSplitsAndKeepResizedProportions()
    {
        var monitor = new MonitorInfo(Guid.NewGuid(), "display", new(0, 0, 1000, 800), new(0, 0, 1000, 800), 96);
        var windows = Enumerable.Range(1, 4).Select(i => new WindowInfo(new(i, 0, 0, new byte[8]),
            monitor.Id, "window", "test", "test", new(0, 0, 100, 100), true, false, false,
            WindowMode.Tiling, 2000, 2000)).ToArray();
        var tree = new SplitTree();
        tree.Reconcile(windows.Take(2).ToArray());
        tree.Resize(windows[0].Id, Direction.Right, .1);
        var rootId = tree.Snapshot!.Id;
        tree.Reconcile(windows);
        Assert.Equal(rootId, tree.Snapshot!.Id);
        Assert.Equal(.6, tree.Snapshot.Ratio, 10);
        var placements = tree.Layout(monitor, 0, 0).Placements;
        Assert.Equal(600, placements[0].Bounds.Width);
        Assert.Equal(new[] { 133, 133, 134 }, placements.Skip(1).Select(p => p.Bounds.Width));
        tree.Reconcile(windows.Take(3).ToArray());
        Assert.Equal(.6, tree.Snapshot.Ratio, 10);
        Assert.Equal(new[] { 600, 200, 200 }, tree.Layout(monitor, 0, 0).Placements.Select(p => p.Bounds.Width));
    }

    [Fact]
    public void ImpossibleOnePixelTilesAndGapsFailInsteadOfDroppingWindows()
    {
        var monitor = new MonitorInfo(Guid.NewGuid(), "display", new(0, 0, 10, 10), new(0, 0, 10, 10), 96);
        var tree = new SplitTree();
        tree.Reconcile(Enumerable.Range(1, 2).Select(i => new WindowInfo(new(i, 0, 0, new byte[8]),
            monitor.Id, "window", "test", "test", new(0, 0, 100, 100), true, false, false,
            WindowMode.Tiling)).ToArray());
        Assert.Throws<InvalidOperationException>(() => tree.Layout(monitor, 9, 0));
        Assert.Equal(2, tree.Layout(monitor, 8, 0).Placements.Count);
    }
}