using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class WorkspaceManagerTests
{
    [Fact]
    public async Task FloatingMonitorTransferRelocatesPhysicalBoundsToDestination()
    {
        await using var fake = new FakeWindowPlatform();
        var target = new MonitorInfo(Guid.NewGuid(), "second", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 144);
        fake.Desktop = fake.Desktop with { Monitors = [.. fake.Desktop.Monitors, target] };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        var id = fake.Desktop.Windows[0].Id;
        await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Floating), timeout.Token);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.MoveToMonitor, id, MonitorId: target.Id), timeout.Token)).Success);
        var actual = engine.Current!.Desktop.Windows.Single(window => window.Id == id).Bounds;
        Assert.InRange(actual.X, target.WorkArea.X, target.WorkArea.X + target.WorkArea.Width - actual.Width);
        Assert.InRange(actual.Y, target.WorkArea.Y, target.WorkArea.Y + target.WorkArea.Height - actual.Height);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task SwitchingHidesWithTrackedIntentAndShutdownRestores()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("one"), new("two")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += _ => ready.TrySetResult();
        var run = engine.RunAsync(timeout.Token);
        await ready.Task.WaitAsync(timeout.Token);
        var second = engine.Current!.Workspaces!.Single(workspace => workspace.Name == "two");
        var result = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: second.Id), timeout.Token);
        Assert.True(result.Success);
        Assert.All(engine.Current!.Desktop.Windows, window => { Assert.False(window.Visible); Assert.True(window.ManagerHidden); });
        Assert.Equal(2, engine.Current.Workspaces!.Sum(workspace => workspace.WindowIds.Count));
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
        Assert.All(fake.Desktop.Windows, window => { Assert.True(window.Visible); Assert.False(window.ManagerHidden); });
        Assert.Empty(engine.ShutdownFailures);
    }

    [Fact]
    public async Task FailedVisibilitySwitchRollsBackLogicalActivation()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("one"), new("two")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += _ => ready.TrySetResult();
        var run = engine.RunAsync(timeout.Token);
        await ready.Task.WaitAsync(timeout.Token);
        var original = engine.Current!.Workspaces!.Single(workspace => workspace.Active).Id;
        fake.FailActions = true;
        var second = engine.Current.Workspaces!.Single(workspace => !workspace.Active).Id;
        var result = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: second), timeout.Token);
        Assert.False(result.Success);
        Assert.Equal(original, engine.Current!.Workspaces!.Single(workspace => workspace.Active).Id);
        Assert.All(fake.Desktop.Windows, window => Assert.True(window.Visible));
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }
}