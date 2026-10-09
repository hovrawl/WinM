using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class DraggingTests
{
    [Fact]
    public async Task DragMovesFreelyAndDropSwapsTilesWithoutExplicitRedraw()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows[0];
        var target = engine.Current.Desktop.Windows[1];
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        fake.Plans.Clear();
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window =>
            window.Id == source.Id ? window with { Bounds = target.Bounds } : window).ToArray() };
        fake.Notify();
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(target.Bounds, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Placements), placement => placement.WindowId == source.Id);

        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += snapshot =>
        {
            if (snapshot.Desktop.Windows.Single(window => window.Id == target.Id).Bounds == source.Bounds)
                dropped.TrySetResult();
        };
        fake.NotifyMove(source.Id, false);
        await dropped.Task.WaitAsync(stop.Token);
        Assert.Equal(target.Bounds, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token)).Success);
        await run;
    }

    [Fact]
    public async Task DropOnAnotherMonitorTransfersWorkspaceAndReflowsBothMonitors()
    {
        await using var fake = new FakeWindowPlatform();
        var destination = new MonitorInfo(Guid.NewGuid(), "second", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96);
        fake.Desktop = fake.Desktop with { Monitors = fake.Desktop.Monitors.Append(destination).ToArray() };
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows[0];
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window => window.Id == source.Id
            ? window with { MonitorId = destination.Id, Bounds = new(200, 200, 400, 400) } : window).ToArray() };
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += snapshot =>
        {
            if (snapshot.Workspaces!.Any(workspace => workspace.MonitorId == destination.Id && workspace.WindowIds.Contains(source.Id)))
                dropped.TrySetResult();
        };
        fake.NotifyMove(source.Id, false);
        await dropped.Task.WaitAsync(stop.Token);
        Assert.Equal(new PixelRect(8, 8, 984, 834), engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.Equal(new PixelRect(-992, 8, 984, 834), engine.Current.Desktop.Windows.Single(window => window.Id != source.Id).Bounds);
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FloatingAndPausedDropsKeepUserGeometryWithoutSwappingTiles(bool paused)
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows[0];
        await engine.ExecuteAsync(paused ? new(CommandKind.Pause, Paused: true)
            : new(CommandKind.SetMode, source.Id, Mode: WindowMode.Floating), stop.Token);
        var layout = engine.Current!.Trees.Values.Single();
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        var desired = new PixelRect(-700, 200, 300, 300);
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window =>
            window.Id == source.Id ? window with { Bounds = desired } : window).ToArray() };
        fake.NotifyMove(source.Id, false);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(desired, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.Equal(layout, engine.Current.Trees.Values.Single());
        Assert.Contains(fake.Plans, plan => plan.CompletedMoves?.Contains(source.Id) == true);
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledMoveOrResizeDoesNotSwapTiles(bool resize)
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows[0];
        var layout = engine.Current.Trees.Values.Single();
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        if (resize)
            fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window => window.Id == source.Id
                ? window with { Bounds = source.Bounds with { Width = source.Bounds.Width + 100 } } : window).ToArray() };
        fake.NotifyMove(source.Id, false);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(source.Bounds, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.Equal(layout, engine.Current.Trees.Values.Single());
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }

    [Fact]
    public async Task DestroyedWindowDuringDragDoesNotPreventRemainingWindowReflow()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows[0];
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Where(window => window.Id != source.Id).ToArray() };
        fake.NotifyMove(source.Id, false);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(new PixelRect(-992, 8, 984, 834), Assert.Single(engine.Current!.Desktop.Windows).Bounds);
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }
}