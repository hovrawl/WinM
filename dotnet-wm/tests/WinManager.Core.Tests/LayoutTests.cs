using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class LayoutTests
{
    private static readonly Guid MonitorId = new("00000000-0000-0000-0000-000000000100");
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private static WindowInfo Window(int value, int minimumWidth = 1, int minimumHeight = 1) =>
        new(Id(value), MonitorId, "window", "test", "test", new(0, 0, 100, 100),
            true, false, false, WindowMode.Tiling, minimumWidth, minimumHeight);

    private static MonitorInfo Monitor(int width = 1000, int height = 800, uint dpi = 96,
        int x = 0, int y = 0) => new(MonitorId, "display", new(x, y, width, height),
        new(x, y, width, height), dpi);

    private static SplitTree Tree(params WindowInfo[] windows)
    {
        var tree = new SplitTree();
        tree.Reconcile(windows);
        return tree;
    }

    private static IEnumerable<SplitNodeInfo> Nodes(SplitNodeInfo? node)
    {
        if (node is null) yield break;
        yield return node;
        foreach (var child in Nodes(node.First)) yield return child;
        foreach (var child in Nodes(node.Second)) yield return child;
    }

    [Fact]
    public void ReconcileExcludesIneligibleWindowsAndIsIndependentOfInputOrder()
    {
        WindowInfo[] windows =
        [
            Window(2), Window(1), Window(3) with { Visible = false },
            Window(4) with { Minimized = true }, Window(5) with { Fullscreen = true },
            Window(6) with { Mode = WindowMode.Floating }
        ];
        var tree = Tree(windows);
        Assert.Equal(Id(1), tree.Snapshot!.First!.WindowId);
        Assert.Equal(Id(2), tree.Snapshot.Second!.WindowId);
        var snapshot = tree.Snapshot;
        tree.Reconcile(windows.Reverse().ToArray());
        Assert.Equal(snapshot, tree.Snapshot);
        Assert.Equal(SplitOrientation.Horizontal, tree.Snapshot.Orientation);
    }

    [Fact]
    public void ReconcilePreservesRatiosAndIdsAndCollapsesRemovedSiblings()
    {
        var tree = Tree(Window(1), Window(2), Window(3));
        Assert.True(tree.Resize(Id(1), Direction.Right, .2));
        var snapshot = tree.Snapshot!;
        tree.Reconcile([Window(3), Window(2), Window(1), Window(4)]);
        Assert.Equal(snapshot.Id, tree.Snapshot!.Id);
        Assert.Equal(snapshot.Ratio, tree.Snapshot.Ratio);
        Assert.Equal(snapshot.First, tree.Snapshot.First);
        var survivingLeaf = Nodes(tree.Snapshot).Single(n => n.WindowId == Id(2));
        tree.Reconcile([Window(2)]);
        Assert.Equal(survivingLeaf, tree.Snapshot);
        tree.Reconcile([]);
        Assert.Null(tree.Snapshot);
        Assert.Empty(tree.Layout(Monitor(), 0, 0).Placements);
    }

    [Fact]
    public void SnapshotIsImmutableAndSwapPreservesTreeShapeAndRatios()
    {
        var tree = Tree(Window(1), Window(2));
        var before = tree.Snapshot!;
        Assert.True(tree.Swap(Id(1), Id(2)));
        Assert.Equal(Id(1), before.First!.WindowId);
        Assert.Equal(before.Id, tree.Snapshot!.Id);
        Assert.Equal(before.First.Id, tree.Snapshot.First!.Id);
        Assert.Equal(before.Ratio, tree.Snapshot.Ratio);
        Assert.Equal(Id(2), tree.Snapshot.First.WindowId);
        Assert.Equal(Id(1), tree.Snapshot.Second!.WindowId);
        Assert.False(tree.Swap(Id(1), Id(1)));
        Assert.False(tree.Swap(Id(1), Id(99)));
    }

    [Fact]
    public void SoleLeafOrientationAppliesToNextSplitAndNearestParentChangesOnly()
    {
        var tree = Tree(Window(1));
        tree.SetOrientation(Id(1), SplitOrientation.Vertical);
        tree.Reconcile([Window(1), Window(2), Window(3)]);
        Assert.Equal(SplitOrientation.Vertical, tree.Snapshot!.Orientation);
        Assert.Equal(SplitOrientation.Horizontal, tree.Snapshot.Second!.Orientation);
        tree.SetOrientation(Id(3), SplitOrientation.Vertical);
        Assert.Equal(SplitOrientation.Vertical, tree.Snapshot.Second!.Orientation);
        tree.SetOrientation(Id(99), SplitOrientation.Horizontal);
        Assert.Equal(SplitOrientation.Vertical, tree.Snapshot.Orientation);
    }

    [Fact]
    public void ResizeUsesNearestMatchingAxisAndClampsRatio()
    {
        var tree = Tree(Window(1));
        tree.SetOrientation(Id(1), SplitOrientation.Vertical);
        tree.Reconcile([Window(1), Window(2), Window(3)]);
        Assert.True(tree.Resize(Id(3), Direction.Left, .2));
        Assert.Equal(.3, tree.Snapshot!.Second!.Ratio, 10);
        Assert.Equal(.5, tree.Snapshot.Ratio);
        Assert.True(tree.Resize(Id(3), Direction.Down, 2));
        Assert.Equal(.9, tree.Snapshot.Ratio);
        Assert.False(tree.Resize(Id(3), Direction.Down, .1));
        Assert.True(tree.Resize(Id(3), Direction.Up, 2));
        Assert.Equal(.1, tree.Snapshot.Ratio);
        Assert.False(tree.Resize(Id(99), Direction.Left, .1));
        Assert.False(Tree(Window(1)).Resize(Id(1), Direction.Right, .1));
    }

    [Fact]
    public void ResizeDoesNotChangeMoreDistantAncestorsOnTheSameAxis()
    {
        var tree = Tree(Window(1), Window(2), Window(3));
        Assert.True(tree.Resize(Id(3), Direction.Right, .15));
        Assert.Equal(1.0 / 3, tree.Snapshot!.Ratio);
        Assert.Equal(.65, tree.Snapshot.Second!.Ratio, 10);
        Assert.True(tree.Resize(Id(3), Direction.Left, .1));
        Assert.Equal(.55, tree.Snapshot.Second.Ratio, 10);
        Assert.False(tree.Resize(Id(3), Direction.Down, .1));
    }

    [Fact]
    public void EligibilityTransitionsCollapseBranchesAndRetainSurvivingSubtrees()
    {
        var tree = Tree(Window(1), Window(2), Window(3));
        var survivingBranch = tree.Snapshot!.Second;
        tree.Reconcile([Window(1) with { Mode = WindowMode.Floating }, Window(2), Window(3)]);
        Assert.Equal(survivingBranch, tree.Snapshot);
        var retainedIds = Nodes(tree.Snapshot).Select(n => n.Id).ToArray();
        tree.Reconcile([Window(3), Window(2), Window(1)]);
        foreach (var id in retainedIds) Assert.Contains(Nodes(tree.Snapshot), n => n.Id == id);
        Assert.Equal(new[] { Id(2), Id(3), Id(1) },
            Nodes(tree.Snapshot).Where(n => n.WindowId is not null).Select(n => n.WindowId!.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResizeRejectsNonPositiveOrNonFiniteAmounts(double amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Tree(Window(1), Window(2)).Resize(Id(1), Direction.Right, amount));
    }

    [Fact]
    public void LayoutConvertsDipGapsAndAssignsRoundingRemainderAtNegativeCoordinates()
    {
        var tree = Tree(Window(1), Window(2));
        var plan = tree.Layout(Monitor(101, 83, 144, -200, -100), 3, 3);
        Assert.Equal(new PixelRect(-195, -95, 43, 73), plan.Placements[0].Bounds);
        Assert.Equal(new PixelRect(-147, -95, 43, 73), plan.Placements[1].Bounds);
        plan = tree.Layout(Monitor(101, 83, 96, -200, -100), 0, 0);
        Assert.Equal(new PixelRect(-200, -100, 50, 83), plan.Placements[0].Bounds);
        Assert.Equal(new PixelRect(-150, -100, 51, 83), plan.Placements[1].Bounds);
        Assert.Equal(plan.Placements, tree.Layout(Monitor(101, 83, 96, -200, -100), 0, 0).Placements);
    }

    [Fact]
    public void NativeCornerResizeUpdatesBothNestedAxesWithDpiScaledGaps()
    {
        var tree = Tree(Window(1));
        tree.SetOrientation(Id(1), SplitOrientation.Vertical);
        tree.Reconcile([Window(1), Window(2), Window(3)]);
        var monitor = Monitor(1000, 800, 144, -1200, -900);
        var before = tree.Layout(monitor, 8, 8).Placements.ToDictionary(placement => placement.WindowId);
        var source = before[Id(3)].Bounds;
        var desired = source with
        {
            X = source.X + 100, Y = source.Y + 80,
            Width = source.Width - 100, Height = source.Height - 80
        };
        Assert.True(tree.ResizeToBounds(Id(3), desired, monitor, 8, 8));
        var after = tree.Layout(monitor, 8, 8).Placements.ToDictionary(placement => placement.WindowId);
        Assert.Equal(desired, after[Id(3)].Bounds);
        Assert.Equal(before[Id(1)].Bounds.Height + 80, after[Id(1)].Bounds.Height);
        Assert.Equal(before[Id(2)].Bounds.Width + 100, after[Id(2)].Bounds.Width);
        Assert.Equal(after[Id(2)].Bounds.Y, desired.Y);
        Assert.Equal(after[Id(2)].Bounds.X + after[Id(2)].Bounds.Width + 12, desired.X);
        Assert.False(tree.ResizeToBounds(Id(3), desired, monitor, 8, 8));
    }

    [Fact]
    public void NativeResizeOfMiddleTileUpdatesBothBoundariesWithoutAccumulatingRounding()
    {
        var tree = Tree(Window(1), Window(2), Window(3));
        var monitor = Monitor(1001);
        var before = tree.Layout(monitor, 8, 8).Placements.ToDictionary(placement => placement.WindowId);
        var source = before[Id(2)].Bounds;
        var desired = source with { X = source.X + 30, Width = source.Width - 70 };
        Assert.True(tree.ResizeToBounds(Id(2), desired, monitor, 8, 8));
        var after = tree.Layout(monitor, 8, 8).Placements.ToDictionary(placement => placement.WindowId);
        Assert.Equal(desired, after[Id(2)].Bounds);
        Assert.Equal(before[Id(1)].Bounds.Width + 30, after[Id(1)].Bounds.Width);
        Assert.Equal(before[Id(3)].Bounds.Width + 40, after[Id(3)].Bounds.Width);
        Assert.False(tree.ResizeToBounds(Id(2), desired, monitor, 8, 8));
        Assert.True(tree.ResizeToBounds(Id(2), source, monitor, 8, 8));
        Assert.Equal(before.Values.OrderBy(placement => placement.WindowId),
            tree.Layout(monitor, 8, 8).Placements.OrderBy(placement => placement.WindowId));
    }

    [Fact]
    public void NativeResizeRespectsMinimumSizesRatioLimitsAndMissingSplits()
    {
        var tree = Tree(Window(1, 200), Window(2, 300));
        var monitor = Monitor();
        Assert.True(tree.ResizeToBounds(Id(1), new(0, 0, 50, 800), monitor, 0, 0));
        Assert.Equal(.1, tree.Snapshot!.Ratio);
        Assert.Equal(200, tree.Layout(monitor, 0, 0).Placements[0].Bounds.Width);
        Assert.True(tree.ResizeToBounds(Id(1), new(0, 0, 950, 800), monitor, 0, 0));
        Assert.Equal(.9, tree.Snapshot.Ratio);
        Assert.Equal(300, tree.Layout(monitor, 0, 0).Placements[1].Bounds.Width);
        Assert.False(tree.ResizeToBounds(Id(99), new(0, 0, 500, 800), monitor, 0, 0));
        Assert.False(tree.ResizeToBounds(Id(1), new(0, 0, 0, 800), monitor, 0, 0));
        Assert.False(Tree(Window(1)).ResizeToBounds(Id(1), new(0, 0, 500, 800), monitor, 0, 0));
    }

    [Fact]
    public void LayoutUsesWorkAreaAndNotMonitorBounds()
    {
        var monitor = Monitor() with { WorkArea = new(-500, 20, 900, 700) };
        Assert.Equal(new PixelRect(-490, 30, 880, 680),
            Tree(Window(1)).Layout(monitor, 10, 10).Placements.Single().Bounds);
    }

    [Fact]
    public void MinimumSizesOverridePreferredRatioWithoutChangingStoredRatio()
    {
        var tree = Tree(Window(1, 70, 30), Window(2, 20, 30));
        tree.Resize(Id(1), Direction.Left, .4);
        var plan = tree.Layout(Monitor(100, 30), 10, 0);
        Assert.Equal(70, plan.Placements[0].Bounds.Width);
        Assert.Equal(20, plan.Placements[1].Bounds.Width);
        Assert.Equal(.1, tree.Snapshot!.Ratio, 10);
        tree.Reconcile([Window(1, 75, 30), Window(2, 20, 30)]);
        var constrained = tree.Layout(Monitor(100, 30), 10, 0);
        Assert.Equal(Id(1), Assert.Single(constrained.Placements).WindowId);
        Assert.Equal(new[] { Id(2) }, constrained.OverflowWindowIds);
    }

    [Fact]
    public void MinimumSizesFollowWindowIdentityAfterSwapAndPlacementsAreReadOnly()
    {
        var tree = Tree(Window(1, 70), Window(2, 20));
        tree.Swap(Id(1), Id(2));
        var plan = tree.Layout(Monitor(90, 20), 0, 0);
        Assert.Equal(Id(2), plan.Placements[0].WindowId);
        Assert.Equal(20, plan.Placements[0].Bounds.Width);
        Assert.Equal(Id(1), plan.Placements[1].WindowId);
        Assert.Equal(70, plan.Placements[1].Bounds.Width);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<WindowPlacement>)plan.Placements).Add(new(Id(99), new(0, 0, 1, 1))));
    }

    [Fact]
    public void NestedMinimumSizesPropagateAcrossBothAxes()
    {
        var tree = Tree(Window(1, 40, 60));
        tree.SetOrientation(Id(1), SplitOrientation.Vertical);
        tree.Reconcile([Window(1, 40, 60), Window(2, 70, 30), Window(3, 20, 40)]);
        var plan = tree.Layout(Monitor(95, 105), 5, 0);
        Assert.Equal(new[] { Id(1), Id(2), Id(3) }, plan.Placements.Select(placement => placement.WindowId));
        Assert.Empty(plan.OverflowWindowIds!);
        Assert.True(plan.Placements[0].Bounds.Width >= 40 && plan.Placements[0].Bounds.Height >= 60);
        Assert.True(plan.Placements[1].Bounds.Width >= 70 && plan.Placements[1].Bounds.Height >= 30);
        Assert.True(plan.Placements[2].Bounds.Width >= 20 && plan.Placements[2].Bounds.Height >= 40);
        Assert.Equal(new[] { Id(1), Id(2) },
            tree.Layout(Monitor(94, 105), 5, 0).Placements.Select(placement => placement.WindowId));
        Assert.Equal(new[] { Id(1), Id(2) },
            tree.Layout(Monitor(95, 104), 5, 0).Placements.Select(placement => placement.WindowId));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeGapsAreRejectedEvenForEmptyTrees(int inner, int outer)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitTree().Layout(Monitor(), inner, outer));
    }

    [Fact]
    public void OverflowSkipsNonFittingLeavesButKeepsLaterFittingLeavesAndSavedTree()
    {
        var tree = Tree(Window(1, 70), Window(2, 70), Window(3, 20));
        tree.Resize(Id(1), Direction.Right, .1);
        var snapshot = tree.Snapshot;
        var plan = tree.Layout(Monitor(100, 100), 5, 0);
        Assert.Equal(new[] { Id(1), Id(3) }, plan.Placements.Select(p => p.WindowId));
        Assert.Equal(new[] { Id(2) }, plan.OverflowWindowIds);
        Assert.True(plan.Placements[0].Bounds.Width >= 70);
        Assert.True(plan.Placements[1].Bounds.Width >= 20);
        Assert.Same(snapshot, tree.Snapshot);
        Assert.Equal(plan.Placements, tree.Layout(Monitor(100, 100), 5, 0).Placements);
        Assert.Equal(3, tree.Layout(Monitor(1000, 100), 5, 0).Placements.Count);
        Assert.Same(snapshot, tree.Snapshot);
    }

    [Fact]
    public void OverflowHonorsNestedAxesDpiGapsAndNegativeCoordinates()
    {
        var tree = Tree(Window(1, 40, 60));
        tree.SetOrientation(Id(1), SplitOrientation.Vertical);
        tree.Reconcile([Window(1, 40, 60), Window(2, 70, 30), Window(3, 20, 40)]);
        var snapshot = tree.Snapshot;
        var plan = tree.Layout(Monitor(109, 121, 144, -109, -121), 10, 5);
        Assert.Equal(new[] { Id(1), Id(2) }, plan.Placements.Select(p => p.WindowId));
        Assert.Equal(new[] { Id(3) }, plan.OverflowWindowIds);
        Assert.All(plan.Placements, placement =>
        {
            Assert.InRange(placement.Bounds.X, -101, -8 - placement.Bounds.Width);
            Assert.InRange(placement.Bounds.Y, -113, -8 - placement.Bounds.Height);
        });
        Assert.Same(snapshot, tree.Snapshot);
    }

    [Fact]
    public void OverflowCanLeaveAllWindowsUntouchedWhenNoneFit()
    {
        var tree = Tree(Window(1, 101), Window(2, 1, 101));
        var snapshot = tree.Snapshot;
        var plan = tree.Layout(Monitor(100, 100), 0, 0);
        Assert.Empty(plan.Placements);
        Assert.Equal(new[] { Id(1), Id(2) }, plan.OverflowWindowIds);
        Assert.Same(snapshot, tree.Snapshot);
    }

    [Fact]
    public void FittingLayoutSkipsAnOversizedEarlierWindowAndKeepsTheFullTree()
    {
        var tree = Tree(Window(1, 101), Window(2, 50));
        var snapshot = tree.Snapshot;

        var plan = tree.Layout(Monitor(100, 100), 0, 0);

        Assert.Equal(Id(2), Assert.Single(plan.Placements).WindowId);
        Assert.Equal(new[] { Id(1) }, plan.OverflowWindowIds);
        Assert.Equal(new[] { Id(1), Id(2) }, tree.WindowOrder);
        Assert.Same(snapshot, tree.Snapshot);
    }

    [Fact]
    public void FittingLayoutKeepsExistingPlacementsAndReportsWindowsThatNoLongerFit()
    {
        var tree = Tree(Window(1, 70), Window(2, 20));
        var strict = tree.Layout(Monitor(100, 100), 5, 0);
        Assert.Equal(2, strict.Placements.Count);
        Assert.Empty(strict.OverflowWindowIds!);
        tree.Reconcile([Window(1, 7000), Window(2, 2000)]);
        var overflow = tree.Layout(Monitor(100, 100), 5, 0);
        Assert.Empty(overflow.Placements);
        Assert.Equal(new[] { Id(1), Id(2) }, overflow.OverflowWindowIds);
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(10, 10), 0, 5));
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(x: int.MaxValue), 0, 0));
    }

    [Fact]
    public void ImpossibleOrOverflowingAreasFailWithoutInvalidRectangles()
    {
        var tree = Tree(Window(1));
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(10, 10), 0, 5));
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(0, 10), 0, 0));
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(x: int.MaxValue), 0, 0));
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(dpi: uint.MaxValue), int.MaxValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Layout(Monitor(dpi: 0), 0, 0));
    }

    [Fact]
    public void NeighborIncludesFloatingAndExcludesInvisibleMinimizedAndFullscreen()
    {
        WindowInfo[] windows =
        [
            Window(1), Window(2) with { Bounds = new(150, 0, 100, 100), Mode = WindowMode.Floating },
            Window(3) with { Bounds = new(100, 0, 100, 100), Visible = false },
            Window(4) with { Bounds = new(100, 0, 100, 100), Minimized = true },
            Window(5) with { Bounds = new(100, 0, 100, 100), Fullscreen = true },
            Window(6) with { Bounds = new(0, -200, 100, 100) }
        ];
        var tree = Tree(windows);
        Assert.Equal(Id(2), tree.Neighbor(Id(1), Direction.Right, windows));
        Assert.Equal(Id(6), tree.Neighbor(Id(1), Direction.Up, windows));
        Assert.Null(tree.Neighbor(Id(1), Direction.Left, windows));
        Assert.Null(tree.Neighbor(Id(99), Direction.Right, windows));
        Assert.Null(tree.Neighbor(Id(1), Direction.Right,
            windows.Where(w => w.Mode == WindowMode.Tiling).ToArray()));
    }

    [Fact]
    public void NeighborUsesDistanceAndStableGuidTieBreaking()
    {
        WindowInfo[] windows =
        [
            Window(1), Window(3) with { Bounds = new(100, -100, 100, 100) },
            Window(2) with { Bounds = new(100, 100, 100, 100) },
            Window(4) with { Bounds = new(200, 0, 100, 100) }
        ];
        var tree = Tree(windows);
        Assert.Equal(Id(2), tree.Neighbor(Id(1), Direction.Right, windows));
        Assert.Equal(Id(2), tree.Neighbor(Id(1), Direction.Right, windows.Reverse().ToArray()));
    }

    [Fact]
    public void NeighborHandlesNegativeAndExtremeCoordinatesWithoutIntegerOverflow()
    {
        WindowInfo[] windows =
        [
            Window(1) with { Bounds = new(int.MinValue, -200, 10, 10) },
            Window(2) with { Bounds = new(int.MinValue + 20, -200, 10, 10) },
            Window(3) with { Bounds = new(int.MaxValue - 10, -200, 10, 10) },
            Window(4) with { Bounds = new(int.MinValue + 1, -200, 0, 10) }
        ];
        Assert.Equal(Id(2), new SplitTree().Neighbor(Id(1), Direction.Right, windows));
        Assert.Equal(Id(2), new SplitTree().Neighbor(Id(3), Direction.Left, windows));
    }

    [Fact]
    public void InvalidEnumsAreRejectedWithoutChangingTree()
    {
        var tree = Tree(Window(1), Window(2));
        var snapshot = tree.Snapshot;
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.SetOrientation(Id(1), (SplitOrientation)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Resize(Id(1), (Direction)99, .1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Neighbor(Id(1), (Direction)99, [Window(1)]));
        Assert.Equal(snapshot, tree.Snapshot);
    }

    [Fact]
    public void RepeatedEditsKeepUniqueLeavesAndNonOverlappingValidPlacements()
    {
        var windows = Enumerable.Range(1, 12).Select(i => Window(i, 3, 4)).ToArray();
        var tree = Tree(windows);
        for (var i = 1; i <= 12; i++)
        {
            tree.SetOrientation(Id(i), i % 2 == 0 ? SplitOrientation.Vertical : SplitOrientation.Horizontal);
            tree.Resize(Id(i), i % 2 == 0 ? Direction.Up : Direction.Right, .17);
            tree.Swap(Id(i), Id(13 - i));
        }

        var leaves = Nodes(tree.Snapshot).Where(n => n.WindowId is not null).ToArray();
        Assert.Equal(12, leaves.Length);
        Assert.Equal(12, leaves.Select(n => n.WindowId).Distinct().Count());
        Assert.Equal(23, Nodes(tree.Snapshot).Select(n => n.Id).Distinct().Count());
        var placements = tree.Layout(Monitor(20000, 20000, 120, -10000, -10000), 3, 7).Placements;
        Assert.Equal(12, placements.Count);
        foreach (var placement in placements)
        {
            Assert.True(placement.Bounds.Width >= 3 && placement.Bounds.Height >= 4);
            Assert.InRange(placement.Bounds.X, -9991, 9991 - placement.Bounds.Width);
            Assert.InRange(placement.Bounds.Y, -9991, 9991 - placement.Bounds.Height);
        }

        foreach (var a in placements)
        foreach (var b in placements.Where(b => b.WindowId != a.WindowId))
            Assert.True(a.Bounds.X + a.Bounds.Width <= b.Bounds.X || b.Bounds.X + b.Bounds.Width <= a.Bounds.X ||
                        a.Bounds.Y + a.Bounds.Height <= b.Bounds.Y || b.Bounds.Y + b.Bounds.Height <= a.Bounds.Y);
    }

    [Fact]
    public void LayeredLayoutUsesFullHeightPagesAndCompressesHorizontalSpacingForLargeStacks()
    {
        var windows = Enumerable.Range(1, 12).Select(index => Window(index)).ToArray();
        var monitor = Monitor(1200, 900, 192, -1400, 300) with
        {
            WorkArea = new(-1380, 320, 1000, 700)
        };

        var plan = LayeredLayout.Layout(monitor, windows, 8);

        Assert.Empty(plan.OverflowWindowIds!);
        Assert.Equal(windows.Select(window => window.Id), plan.Placements.Select(placement => placement.WindowId));
        var first = plan.Placements[0].Bounds;
        var last = plan.Placements[^1].Bounds;
        Assert.Equal(monitor.WorkArea.X + 16, first.X);
        Assert.Equal(monitor.WorkArea.Y + 16, first.Y);
        Assert.Equal(194, last.X - first.X);
        Assert.Equal(first.Y, last.Y);
        Assert.All(plan.Placements, placement =>
        {
            Assert.Equal(774, placement.Bounds.Width);
            Assert.Equal(668, placement.Bounds.Height);
            Assert.InRange(placement.Bounds.X, monitor.WorkArea.X,
                monitor.WorkArea.X + monitor.WorkArea.Width - placement.Bounds.Width);
            Assert.Equal(first.Y, placement.Bounds.Y);
            Assert.InRange(placement.Bounds.Y, monitor.WorkArea.Y,
                monitor.WorkArea.Y + monitor.WorkArea.Height - placement.Bounds.Height);
        });
    }

    [Fact]
    public void LayeredLayoutUsesConfiguredWindowWidthAndDpiScaledHorizontalOffset()
    {
        var windows = Enumerable.Range(1, 3).Select(index => Window(index)).ToArray();
        var monitor = Monitor(1200, 900, 144, -100, 10) with
        {
            WorkArea = new(-90, 20, 1000, 700)
        };

        var plan = LayeredLayout.Layout(monitor, windows, 8, new LayeredLayoutOptions(0.6, 50));

        Assert.Empty(plan.OverflowWindowIds!);
        Assert.All(plan.Placements, placement =>
        {
            Assert.Equal(586, placement.Bounds.Width);
            Assert.Equal(676, placement.Bounds.Height);
            Assert.Equal(32, placement.Bounds.Y);
        });
        Assert.Equal(75, plan.Placements[1].Bounds.X - plan.Placements[0].Bounds.X);
        Assert.Equal(75, plan.Placements[2].Bounds.X - plan.Placements[1].Bounds.X);
    }

    [Fact]
    public void LayeredLayoutAllowsZeroOffsetToOverlayAllPages()
    {
        var windows = Enumerable.Range(1, 4).Select(index => Window(index)).ToArray();

        var plan = LayeredLayout.Layout(Monitor(), windows, 8, new LayeredLayoutOptions(0.8, 0));

        Assert.Equal(4, plan.Placements.Count);
        Assert.All(plan.Placements, placement => Assert.Equal(plan.Placements[0].Bounds, placement.Bounds));
    }

    [Fact]
    public void LayeredLayoutRejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayeredLayout.Layout(Monitor(), [], 8,
            new LayeredLayoutOptions(double.NaN, 32)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayeredLayout.Layout(Monitor(), [], 8,
            new LayeredLayoutOptions(0.8, 257)));
    }

    [Fact]
    public void LayeredLayoutOnlyOverflowsPagesIndividuallyTooLargeForWorkArea()
    {
        var windows = new[] { Window(1, 1200), Window(2), Window(3) };

        var plan = LayeredLayout.Layout(Monitor(), windows, 8);

        Assert.Equal(new[] { Id(2), Id(3) }, plan.Placements.Select(placement => placement.WindowId));
        Assert.Equal([Id(1)], plan.OverflowWindowIds);
    }

    [Fact]
    public void LayeredLayoutReducesMarginsAndHorizontalSpreadToRespectFittingMinimums()
    {
        var monitor = Monitor(400, 300, 144, -1000, 0) with { WorkArea = new(-1000, 20, 400, 300) };
        var windows = new[] { Window(1, 350, 270), Window(2, 300, 250) };

        var plan = LayeredLayout.Layout(monitor, windows, 30);

        Assert.Empty(plan.OverflowWindowIds!);
        Assert.Equal(2, plan.Placements.Count);
        Assert.All(plan.Placements, placement =>
        {
            var window = windows.Single(candidate => candidate.Id == placement.WindowId);
            Assert.True(placement.Bounds.Width >= window.MinimumWidth);
            Assert.True(placement.Bounds.Height >= window.MinimumHeight);
        });
        Assert.Equal(-985, plan.Placements[0].Bounds.X);
        Assert.Equal(20, plan.Placements[1].Bounds.X - plan.Placements[0].Bounds.X);
        Assert.Equal(35, plan.Placements[0].Bounds.Y);
        Assert.Equal(plan.Placements[0].Bounds.Y, plan.Placements[1].Bounds.Y);
    }
}