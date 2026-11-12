using HomeDesk.Contracts;
using Xunit;

namespace HomeDesk.Core.Tests;

public sealed class SpatialCommandTests
{
    [Fact]
    public async Task FocusMonitorUsesConfiguredDeviceOrderAndReportsReservedDisconnectedSlots()
    {
        await using var fake = new FakeWindowPlatform();
        var firstMonitor = fake.Desktop.Monitors.Single();
        var secondMonitor = Monitor("other", new(0, 0, 1000, 900));
        var firstWindow = Window("first", firstMonitor.Id, new(-900, 20, 200, 200)) with { Mode = WindowMode.Tiling };
        var secondWindow = Window("second", secondMonitor.Id, new(300, 250, 200, 200)) with
        {
            Mode = WindowMode.Tiling
        };
        fake.Desktop = new([firstMonitor, secondMonitor], [firstWindow, secondWindow], firstWindow.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One"), new("Two")],
            monitorDeviceOrder: ["offline-display", "other", "display"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);

            Assert.Equal("not_found", (await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 1), timeout.Token)).ErrorCode);
            var result = await engine.ExecuteAsync(new(CommandKind.FocusMonitor, MonitorNumber: 2), timeout.Token);

            Assert.True(result.Success);
            Assert.Equal(secondWindow.Id, engine.Current!.Desktop.FocusedWindowId);
            Assert.Equal("not_found", (await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 10), timeout.Token)).ErrorCode);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Theory]
    [InlineData(WorkspaceLayout.Split, WindowMode.Floating)]
    [InlineData(WorkspaceLayout.Layered, WindowMode.Tiling)]
    public async Task FocusMonitorPreservesLastFocusedWindowForWorkspace(WorkspaceLayout layout, WindowMode mode)
    {
        await using var fake = new FakeWindowPlatform();
        var left = Monitor("left", new(-1000, 0, 1000, 900));
        var right = Monitor("right", new(0, 0, 1000, 900));
        var leftWindow = Window("left", left.Id, new(-900, 100, 200, 200));
        var remembered = Window("remembered", right.Id, new(50, 50, 200, 200)) with { Mode = mode };
        var closest = Window("closest", right.Id, new(400, 350, 200, 200)) with { Mode = mode };
        fake.Desktop = new([left, right], [leftWindow, remembered, closest], leftWindow.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions:
        [
            new("Left", Layout: layout),
            new("Right", Layout: layout)
        ], monitorDeviceOrder: ["left", "right"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);

            Assert.True((await engine.ExecuteAsync(new(CommandKind.Focus, remembered.Id), timeout.Token)).Success);
            Assert.True((await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 2), timeout.Token)).Success);
            Assert.Equal(remembered.Id, engine.Current!.Desktop.FocusedWindowId);

            Assert.True((await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 1), timeout.Token)).Success);
            Assert.Equal(leftWindow.Id, engine.Current!.Desktop.FocusedWindowId);
            Assert.True((await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 2), timeout.Token)).Success);
            Assert.Equal(remembered.Id, engine.Current!.Desktop.FocusedWindowId);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusMonitorFallsBackWhenRememberedWindowIsUnavailable(bool minimizeRememberedWindow)
    {
        await using var fake = new FakeWindowPlatform();
        var left = Monitor("left", new(-1000, 0, 1000, 900));
        var right = Monitor("right", new(0, 0, 1000, 900));
        var leftWindow = Window("left", left.Id, new(-900, 100, 200, 200));
        var remembered = Window("remembered", right.Id, new(50, 50, 200, 200));
        var closest = Window("closest", right.Id, new(400, 350, 200, 200));
        fake.Desktop = new([left, right], [leftWindow, remembered, closest], leftWindow.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("Left"), new("Right")],
            monitorDeviceOrder: ["left", "right"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Focus, remembered.Id), timeout.Token)).Success);
            Assert.True((await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 1), timeout.Token)).Success);

            fake.Desktop = fake.Desktop with
            {
                Windows = minimizeRememberedWindow
                    ? fake.Desktop.Windows.Select(window => window.Id == remembered.Id
                        ? window with { Minimized = true }
                        : window).ToArray()
                    : fake.Desktop.Windows.Where(window => window.Id != remembered.Id).ToArray()
            };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.True((await engine.ExecuteAsync(
                new(CommandKind.FocusMonitor, MonitorNumber: 2), timeout.Token)).Success);
            Assert.Equal(closest.Id, engine.Current!.Desktop.FocusedWindowId);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task MoveToMonitorNumberUsesConfiguredDeviceOrderAndActiveWorkspace()
    {
        await using var fake = new FakeWindowPlatform();
        var firstMonitor = fake.Desktop.Monitors.Single();
        var secondMonitor = Monitor("other", new(0, 0, 1000, 900));
        var source = Window("source", firstMonitor.Id, new(-900, 20, 200, 200));
        var target = Window("target", secondMonitor.Id, new(300, 250, 200, 200));
        fake.Desktop = new([firstMonitor, secondMonitor], [source, target], source.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One"), new("Two")],
            monitorDeviceOrder: ["offline-display", "other", "display"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var targetWorkspace =
                engine.Current!.Workspaces!.Single(workspace => workspace.WindowIds.Contains(target.Id));

            Assert.Equal("invalid_argument", (await engine.ExecuteAsync(
                new(CommandKind.MoveToMonitor, WindowId: source.Id, MonitorNumber: 11), timeout.Token)).ErrorCode);
            Assert.Equal("invalid_argument", (await engine.ExecuteAsync(
                new(CommandKind.MoveToMonitor, WindowId: source.Id, MonitorId: secondMonitor.Id, MonitorNumber: 2),
                timeout.Token)).ErrorCode);
            Assert.Equal("not_found", (await engine.ExecuteAsync(
                new(CommandKind.MoveToMonitor, WindowId: source.Id, MonitorNumber: 1), timeout.Token)).ErrorCode);
            var result = await engine.ExecuteAsync(
                new(CommandKind.MoveToMonitor, WindowId: source.Id, MonitorNumber: 2), timeout.Token);

            Assert.True(result.Success);
            Assert.Contains(source.Id, engine.Current!.Workspaces!.Single(workspace =>
                workspace.Id == targetWorkspace.Id).WindowIds);
            Assert.Equal(new PixelRect(400, 350, 200, 200),
                engine.Current.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task DirectionalFocusFallsBackToTheNearestMonitorWhenWorkspaceHasNoNeighbor()
    {
        await using var fake = new FakeWindowPlatform();
        var left = fake.Desktop.Monitors.Single();
        var right = Monitor("right", new(0, 0, 1000, 900));
        var source = Window("source", left.Id, new(-900, 100, 200, 200));
        var destination = Window("destination", right.Id, new(250, 100, 200, 200));
        fake.Desktop = new([left, right], [source, destination], source.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("Left"), new("Right")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);

            var result = await engine.ExecuteAsync(new(CommandKind.Focus, Direction: Direction.Right), timeout.Token);

            Assert.True(result.Success);
            Assert.Equal(destination.Id, engine.Current!.Desktop.FocusedWindowId);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task DirectionalFocusPrefersAnEligibleWindowInTheCurrentWorkspace()
    {
        await using var fake = new FakeWindowPlatform();
        var left = fake.Desktop.Monitors.Single();
        var right = Monitor("right", new(0, 0, 1000, 900));
        var source = Window("source", left.Id, new(-900, 100, 200, 200));
        var local = Window("local", left.Id, new(-600, 100, 200, 200));
        var remote = Window("remote", right.Id, new(250, 100, 200, 200));
        fake.Desktop = new([left, right], [source, local, remote], source.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("Left"), new("Right")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);

            var result = await engine.ExecuteAsync(new(CommandKind.Focus, Direction: Direction.Right), timeout.Token);

            Assert.True(result.Success);
            Assert.Equal(local.Id, engine.Current!.Desktop.FocusedWindowId);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task MoveSwapsFocusedTiledWindowWithAdjacentStackEntryAndDoesNotWrapAtEdge()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = fake.Desktop.Monitors.Single();
        var windows = Enumerable.Range(0, 3).Select(index => Window($"Window {index}", monitor.Id,
                new(-900 + index * 240, 100, 200, 200)) with
            {
                Mode = WindowMode.Tiling
            }).ToArray();
        fake.Desktop = new([monitor], windows, null);
        var engine = new ManagerEngine(fake, workspaceDefinitions:
            [new("One", Layout: WorkspaceLayout.Layered, LayeredLayoutOptions: new(0.8, 32))]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var workspace = Assert.Single(engine.Current!.Workspaces!);
            var originalOrder = workspace.WindowIds.ToArray();
            var originalTreeOrder = TreeWindowOrder(engine.Current.Trees[workspace.Id]);
            Assert.Equal(3, originalTreeOrder.Length);
            var focused = originalTreeOrder[1];
            fake.Desktop = fake.Desktop with { FocusedWindowId = focused };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            var focusedIndex = Array.IndexOf(originalTreeOrder, focused);

            var moved = await engine.ExecuteAsync(new(CommandKind.Move, WindowId: focused, Direction: Direction.Left),
                timeout.Token);

            Assert.True(moved.Success);
            var expectedOrder = originalTreeOrder.ToArray();
            (expectedOrder[focusedIndex - 1], expectedOrder[focusedIndex]) =
                (expectedOrder[focusedIndex], expectedOrder[focusedIndex - 1]);
            Assert.Equal(expectedOrder, TreeWindowOrder(engine.Current!.Trees[workspace.Id]));
            Assert.Equal(focused, engine.Current.Desktop.FocusedWindowId);
            Assert.Equal(WindowMode.Tiling, engine.Current.Desktop.Windows.Single(window => window.Id == focused).Mode);
            Assert.Equal(originalOrder, engine.Current.Workspaces!.Single().WindowIds);

            var movedOrder = TreeWindowOrder(engine.Current.Trees[workspace.Id]);
            var edgeWindow = movedOrder[0];
            var edgeMove = await engine.ExecuteAsync(new(CommandKind.Move, WindowId: edgeWindow,
                Direction: Direction.Left), timeout.Token);

            Assert.False(edgeMove.Success);
            Assert.Equal("not_found", edgeMove.ErrorCode);
            Assert.Equal(movedOrder, TreeWindowOrder(engine.Current.Trees[workspace.Id]));
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task SpatialMoveFloatsAndPlacesAtLocalDirectionalWindowOriginAndStaysStable()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = fake.Desktop.Monitors.Single();
        var origin = Window("origin", monitor.Id, new(-900, 100, 200, 200));
        var neighbor = Window("neighbor", monitor.Id, new(-500, 140, 300, 240));
        fake.Desktop = new([monitor], [origin, neighbor], origin.Id);
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var workspace = engine.Current!.Workspaces!.Single();

            var result =
                await engine.ExecuteAsync(new(CommandKind.MoveSpatial, Direction: Direction.Right), timeout.Token);

            Assert.True(result.Success);
            var moved = engine.Current!.Desktop.Windows.Single(window => window.Id == origin.Id);
            Assert.Equal(WindowMode.Floating, moved.Mode);
            Assert.Equal(new PixelRect(neighbor.Bounds.X, neighbor.Bounds.Y, origin.Bounds.Width, origin.Bounds.Height),
                moved.Bounds);
            Assert.Equal(workspace.WindowIds.Order(), engine.Current.Workspaces!.Single().WindowIds.Order());

            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.Equal(moved.Bounds, engine.Current!.Desktop.Windows.Single(window => window.Id == origin.Id).Bounds);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task SpatialMoveTransfersMembershipToNeighboringMonitorWorkspace()
    {
        await using var fake = new FakeWindowPlatform();
        var left = fake.Desktop.Monitors.Single();
        var right = Monitor("right", new(0, 0, 1000, 900));
        var source = Window("source", left.Id, new(-900, 100, 200, 200));
        var destination = Window("destination", right.Id, new(250, 100, 200, 200));
        fake.Desktop = new([left, right], [source, destination], source.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("Left"), new("Right")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var originalWorkspace =
                engine.Current!.Workspaces!.Single(workspace => workspace.WindowIds.Contains(source.Id));
            var targetWorkspace =
                engine.Current!.Workspaces!.Single(workspace => workspace.WindowIds.Contains(destination.Id));

            var result =
                await engine.ExecuteAsync(new(CommandKind.MoveSpatial, Direction: Direction.Right), timeout.Token);

            Assert.True(result.Success);
            Assert.DoesNotContain(source.Id, engine.Current!.Workspaces!.Single(workspace =>
                workspace.Id == originalWorkspace.Id).WindowIds);
            Assert.Contains(source.Id, engine.Current!.Workspaces!.Single(workspace =>
                workspace.Id == targetWorkspace.Id).WindowIds);
            Assert.Equal(WindowMode.Floating,
                engine.Current.Desktop.Windows.Single(window => window.Id == source.Id).Mode);
            Assert.Equal(new PixelRect(400, 350, 200, 200),
                engine.Current.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task SpatialMoveRejectsPausedStateAndRollsBackModeOnNativeFailure()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = fake.Desktop.Monitors.Single();
        fake.Desktop = fake.Desktop with
        {
            Windows = fake.Desktop.Windows.Select(window => window with { Mode = WindowMode.Tiling }).ToArray()
        };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var source = engine.Current!.Desktop.Windows.OrderBy(window => window.Bounds.X).First();
            var neighbor = engine.Current.Desktop.Windows.Single(window => window.Id != source.Id);
            var direction = neighbor.Bounds.X > source.Bounds.X ? Direction.Right : Direction.Left;
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), timeout.Token)).Success);
            Assert.Equal("not_ready", (await engine.ExecuteAsync(
                new(CommandKind.MoveSpatial, WindowId: source.Id, Direction: direction), timeout.Token)).ErrorCode);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Pause, Paused: false), timeout.Token)).Success);

            fake.FailActions = true;
            var failed = await engine.ExecuteAsync(
                new(CommandKind.MoveSpatial, WindowId: source.Id, Direction: direction), timeout.Token);

            Assert.False(failed.Success);
            Assert.Equal("access_denied", failed.ErrorCode);
            Assert.Equal(WindowMode.Tiling,
                engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Mode);
            Assert.Contains(source.Id, Assert.Single(engine.Current.Workspaces!).WindowIds);
            Assert.Equal(monitor.Id, source.MonitorId);
        }
        finally
        {
            fake.FailActions = false;
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task SpatialMoveRestoresMembershipAndModeAfterCrossMonitorNativeFailure()
    {
        await using var fake = new FakeWindowPlatform();
        var left = fake.Desktop.Monitors.Single();
        var right = Monitor("right", new(0, 0, 1000, 900));
        var source = Window("source", left.Id, new(-900, 100, 200, 200)) with { Mode = WindowMode.Tiling };
        var destination = Window("destination", right.Id, new(250, 100, 200, 200));
        fake.Desktop = new([left, right], [source, destination], source.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("Left"), new("Right")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var originalWorkspace =
                engine.Current!.Workspaces!.Single(workspace => workspace.WindowIds.Contains(source.Id));
            fake.FailActions = true;

            var result =
                await engine.ExecuteAsync(new(CommandKind.MoveSpatial, Direction: Direction.Right), timeout.Token);

            Assert.False(result.Success);
            Assert.Equal("access_denied", result.ErrorCode);
            Assert.Contains(source.Id, engine.Current!.Workspaces!.Single(workspace =>
                workspace.Id == originalWorkspace.Id).WindowIds);
            Assert.Equal(WindowMode.Tiling,
                engine.Current.Desktop.Windows.Single(window => window.Id == source.Id).Mode);
        }
        finally
        {
            fake.FailActions = false;
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    private static WindowInfo Window(string title, Guid monitorId, PixelRect bounds) =>
        new(Guid.NewGuid(), monitorId, title, "test", "test", bounds, true, false, false, WindowMode.Floating);

    private static Guid[] TreeWindowOrder(SplitNodeInfo? node) => node is null
        ? []
        : node.WindowId is { } windowId
            ? [windowId]
            : TreeWindowOrder(node.First).Concat(TreeWindowOrder(node.Second)).ToArray();

    private static MonitorInfo Monitor(string name, PixelRect bounds) =>
        new(Guid.NewGuid(), name, bounds, bounds, 96);
}