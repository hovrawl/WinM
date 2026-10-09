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
        WindowInfo[] windows = [Window(2), Window(1), Window(3) with { Visible = false },
            Window(4) with { Minimized = true }, Window(5) with { Fullscreen = true },
            Window(6) with { Mode = WindowMode.Floating }];
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
        Assert.Equal(.5, tree.Snapshot!.Ratio);
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
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(100, 30), 10, 0));
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
        Assert.Equal(new PixelRect(0, 0, 95, 60), plan.Placements[0].Bounds);
        Assert.Equal(new PixelRect(0, 65, 70, 40), plan.Placements[1].Bounds);
        Assert.Equal(new PixelRect(75, 65, 20, 40), plan.Placements[2].Bounds);
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(94, 105), 5, 0));
        Assert.Throws<InvalidOperationException>(() => tree.Layout(Monitor(95, 104), 5, 0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeGapsAreRejectedEvenForEmptyTrees(int inner, int outer)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitTree().Layout(Monitor(), inner, outer));
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
        WindowInfo[] windows = [Window(1), Window(2) with { Bounds = new(150, 0, 100, 100), Mode = WindowMode.Floating },
            Window(3) with { Bounds = new(100, 0, 100, 100), Visible = false },
            Window(4) with { Bounds = new(100, 0, 100, 100), Minimized = true },
            Window(5) with { Bounds = new(100, 0, 100, 100), Fullscreen = true },
            Window(6) with { Bounds = new(0, -200, 100, 100) }];
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
        WindowInfo[] windows = [Window(1), Window(3) with { Bounds = new(100, -100, 100, 100) },
            Window(2) with { Bounds = new(100, 100, 100, 100) },
            Window(4) with { Bounds = new(200, 0, 100, 100) }];
        var tree = Tree(windows);
        Assert.Equal(Id(2), tree.Neighbor(Id(1), Direction.Right, windows));
        Assert.Equal(Id(2), tree.Neighbor(Id(1), Direction.Right, windows.Reverse().ToArray()));
    }

    [Fact]
    public void NeighborHandlesNegativeAndExtremeCoordinatesWithoutIntegerOverflow()
    {
        WindowInfo[] windows = [Window(1) with { Bounds = new(int.MinValue, -200, 10, 10) },
            Window(2) with { Bounds = new(int.MinValue + 20, -200, 10, 10) },
            Window(3) with { Bounds = new(int.MaxValue - 10, -200, 10, 10) },
            Window(4) with { Bounds = new(int.MinValue + 1, -200, 0, 10) }];
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
}