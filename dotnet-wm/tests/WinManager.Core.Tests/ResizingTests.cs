using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class ResizingTests
{
    [Theory]
    [InlineData(false, false, -100)]
    [InlineData(false, true, -100)]
    [InlineData(true, false, -100)]
    [InlineData(true, true, -100)]
    [InlineData(false, false, 100)]
    public async Task ResizeReflowsNeighborsDuringInteractionAndKeepsGeometryOnRelease(bool vertical, bool second, int delta)
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        if (vertical)
            await engine.ExecuteAsync(new(CommandKind.SetSplit, fake.Desktop.Windows[0].Id,
                Orientation: SplitOrientation.Vertical), stop.Token);
        var ordered = engine.Current!.Desktop.Windows.OrderBy(window => vertical ? window.Bounds.Y : window.Bounds.X).ToArray();
        var source = ordered[second ? 1 : 0];
        var neighbor = ordered[second ? 0 : 1];
        var desired = vertical
            ? source.Bounds with { Y = source.Bounds.Y - (second ? delta : 0), Height = source.Bounds.Height + delta }
            : source.Bounds with { X = source.Bounds.X - (second ? delta : 0), Width = source.Bounds.Width + delta };
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        fake.Plans.Clear();
        var reflowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += snapshot =>
        {
            var bounds = snapshot.Desktop.Windows.Single(window => window.Id == neighbor.Id).Bounds;
            if ((vertical ? bounds.Height : bounds.Width) == (vertical ? neighbor.Bounds.Height : neighbor.Bounds.Width) - delta)
                reflowed.TrySetResult();
        };
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window => window.Id == source.Id
            ? window with { Bounds = desired } : window).ToArray() };
        fake.Notify();
        await reflowed.Task.WaitAsync(stop.Token);
        Assert.Equal(desired, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Placements), placement => placement.WindowId == source.Id);
        fake.NotifyMove(source.Id, false);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(desired, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.Equal(source.Id, second ? engine.Current.Trees.Values.Single()!.Second!.WindowId
            : engine.Current.Trees.Values.Single()!.First!.WindowId);
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FloatingOrPausedResizeKeepsUserBoundsWithoutChangingSplits(bool paused)
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows.OrderBy(window => window.Bounds.X).First();
        await engine.ExecuteAsync(paused ? new(CommandKind.Pause, Paused: true)
            : new(CommandKind.SetMode, source.Id, Mode: WindowMode.Floating), stop.Token);
        var layout = engine.Current!.Trees.Values.Single();
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        var desired = source.Bounds with { Width = source.Bounds.Width - 100 };
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window => window.Id == source.Id
            ? window with { Bounds = desired } : window).ToArray() };
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(layout, engine.Current!.Trees.Values.Single());
        fake.NotifyMove(source.Id, false);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(desired, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        Assert.Equal(layout, engine.Current.Trees.Values.Single());
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }

    [Fact]
    public async Task FinalResizeSampleOnReleaseUpdatesLayoutWithoutAnIntermediateNotification()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(stop.Token);
        await engine.Ready.WaitAsync(stop.Token);
        var source = engine.Current!.Desktop.Windows.OrderBy(window => window.Bounds.X).Last();
        fake.NotifyMove(source.Id, true);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        var desired = source.Bounds with { X = source.Bounds.X + 100, Width = source.Bounds.Width - 100 };
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window => window.Id == source.Id
            ? window with { Bounds = desired } : window).ToArray() };
        fake.NotifyMove(source.Id, false);
        await fake.MoveNotificationRead.Task.WaitAsync(stop.Token);
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(desired, engine.Current!.Desktop.Windows.Single(window => window.Id == source.Id).Bounds);
        var neighbor = engine.Current.Desktop.Windows.Single(window => window.Id != source.Id).Bounds;
        Assert.Equal(neighbor.X + neighbor.Width + 8, desired.X);
        Assert.Equal(source.Id, engine.Current.Trees.Values.Single()!.Second!.WindowId);
        await engine.ExecuteAsync(new(CommandKind.Exit), stop.Token);
        await run;
    }
}