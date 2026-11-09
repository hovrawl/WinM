using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class SpatialNavigationTests
{
    [Theory]
    [InlineData(Direction.Left, -200, 0)]
    [InlineData(Direction.Right, 200, 0)]
    [InlineData(Direction.Up, 0, -200)]
    [InlineData(Direction.Down, 0, 200)]
    public void ClosestWindowInDirectionUsesGeometryAndSupportsNegativeCoordinates(Direction direction,
        int expectedX, int expectedY)
    {
        var origin = new PixelRect(-50, -50, 100, 100);
        var nearest = Window("nearest", new(expectedX - 20, expectedY - 20, 40, 40));
        var farther = Window("farther", new(expectedX * 2 - 20, expectedY * 2 - 20, 40, 40));
        var (behindX, behindY) = direction switch
        {
            Direction.Left => (100, 0),
            Direction.Right => (-100, 0),
            Direction.Up => (0, 100),
            Direction.Down => (0, -100),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        var behind = Window("behind", new(behindX - 20, behindY - 20, 40, 40));

        var target = SpatialNavigation.ClosestWindowInDirection(origin, direction, [farther, behind, nearest]);

        Assert.Equal(nearest.Id, target?.Id);
    }

    [Fact]
    public void DirectionalWindowSearchExcludesOverflowIgnoredAndInvisibleWindowsAndBreaksTiesById()
    {
        var origin = new PixelRect(0, 0, 100, 100);
        var eligible = Window("eligible", new(200, 0, 100, 100)) with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };
        var tied = Window("tied", new(200, 0, 100, 100)) with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        var excluded = new[]
        {
            Window("overflow", new(200, 0, 100, 100)) with { LayoutOverflow = true },
            Window("ignored", new(200, 0, 100, 100)) with { IsPopup = true },
            Window("invisible", new(200, 0, 100, 100)) with { Visible = false }
        };

        Assert.Equal(eligible.Id, SpatialNavigation.ClosestWindowInDirection(origin, Direction.Right,
            excluded.Append(tied).Append(eligible))?.Id);
    }

    [Fact]
    public void MonitorFallbackSelectsTheNearestMonitorInTheRequestedDirection()
    {
        var origin = Monitor("origin", new(-1000, -100, 900, 800));
        var near = Monitor("near", new(-100, -100, 900, 800));
        var far = Monitor("far", new(900, -100, 900, 800));

        Assert.Equal(near.Id, SpatialNavigation.ClosestMonitorInDirection(origin.Bounds, Direction.Right,
            [far, near])?.Id);
        Assert.Null(SpatialNavigation.ClosestMonitorInDirection(origin.Bounds, Direction.Left, [near, far]));
    }

    [Fact]
    public void PlacementPreservesWindowSizeWhenPossibleAndClampsToNegativeWorkArea()
    {
        var window = Window("move", new(-1900, -200, 700, 500));

        var placed = SpatialNavigation.PlaceAt(window, new(-1920, -100, 1000, 800), -1700, -800);
        var centered = SpatialNavigation.CenterIn(window, new(-1920, -100, 1000, 800));

        Assert.Equal(new PixelRect(-1700, -100, 700, 500), placed);
        Assert.Equal(new PixelRect(-1770, 50, 700, 500), centered);
    }

    [Fact]
    public void PlacementRejectsWorkAreasSmallerThanWindowMinimum()
    {
        var window = Window("large", new(0, 0, 500, 300)) with { MinimumWidth = 700 };

        Assert.Throws<InvalidOperationException>(() =>
            SpatialNavigation.CenterIn(window, new(0, 0, 600, 400)));
    }

    private static WindowInfo Window(string title, PixelRect bounds, Guid? monitorId = null) =>
        new(Guid.NewGuid(), monitorId ?? Guid.NewGuid(), title, "test", "test", bounds,
            true, false, false, WindowMode.Tiling);

    private static MonitorInfo Monitor(string name, PixelRect bounds) =>
        new(Guid.NewGuid(), name, bounds, bounds, 96);
}
