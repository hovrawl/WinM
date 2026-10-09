using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class WorkspaceManagerTests
{
    [Fact]
    public async Task PrimaryMinimumSizeOverflowDoesNotBlockAllTilingAndRecoversWhenSpaceReturns()
    {
        await using var fake = new FakeWindowPlatform();
        var primary = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 1920, 1200), new(0, 0, 1920, 1200), 144);
        var secondary = primary with { Id = Guid.NewGuid(), DeviceName = "Secondary", Bounds = new(-1920, 0, 1920, 1200), WorkArea = new(-1920, 0, 1920, 1200) };
        var template = fake.Desktop.Windows[0];
        WindowInfo[] windows =
        [
            template with { Id = new(1, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 974, MinimumHeight = 964 },
            template with { Id = new(2, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 807, MinimumHeight = 909 },
            template with { Id = new(3, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 882, MinimumHeight = 892 },
            template with { Id = new(4, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 542, MinimumHeight = 541 },
            template with { Id = new(5, 0, 0, new byte[8]), MonitorId = secondary.Id }
        ];
        fake.Desktop = new([primary, secondary], windows, null);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("1"), new("2")]);
        var diagnostics = new List<IReadOnlyList<NativeActionFailure>>();
        engine.ActionsFailed += failures => diagnostics.Add(failures);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var current = engine.Current!;
            Assert.NotEqual(windows[0].Bounds, current.Desktop.Windows.Single(w => w.Id == windows[0].Id).Bounds);
            Assert.NotEqual(windows[1].Bounds, current.Desktop.Windows.Single(w => w.Id == windows[1].Id).Bounds);
            Assert.Equal(new PixelRect(966, 12, 465, 1176), current.Desktop.Windows.Single(w => w.Id == windows[2].Id).Bounds);
            Assert.Equal(new PixelRect(1443, 12, 465, 1176), current.Desktop.Windows.Single(w => w.Id == windows[3].Id).Bounds);
            Assert.NotEqual(windows[4].Bounds, current.Desktop.Windows.Single(w => w.Id == windows[4].Id).Bounds);
            Assert.All(current.Desktop.Windows, w => { Assert.True(w.Visible); Assert.False(w.ManagerHidden); Assert.Equal(WindowMode.Tiling, w.Mode); });
            Assert.Equal(4, current.Workspaces!.Single(w => w.Name == "1").WindowIds.Count);
            var result = await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
            Assert.True(result.Success);
            Assert.Empty(diagnostics);
            var widths = current.Desktop.Windows.Where(w => w.MonitorId == primary.Id).Select(w => w.Bounds.Width).ToArray();
            Assert.InRange(widths.Max() - widths.Min(), 0, 1);

            fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(w => w with { MinimumWidth = 100, MinimumHeight = 100 }).ToArray() };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.All(engine.Current!.Desktop.Windows, w => Assert.NotEqual(template.Bounds, w.Bounds));
            fake.Desktop = fake.Desktop with { Windows = windows };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.Empty(diagnostics);

            var destination = engine.Current!.Workspaces!.Single(w => w.Name == "2");
            Assert.True((await engine.ExecuteAsync(new(CommandKind.MoveToWorkspace, windows[3].Id, WorkspaceId: destination.Id), timeout.Token)).Success);
            Assert.Equal(windows[3].Id, Assert.Single(engine.Current!.Workspaces!.Single(w => w.Id == destination.Id).WindowIds));
            Assert.True((await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: destination.Id), timeout.Token)).Success);
            Assert.True(engine.Current!.Workspaces!.Single(w => w.Id == destination.Id).Active);
            Assert.True(engine.Current.Desktop.Windows.Single(w => w.Id == windows[3].Id).Visible);
            Assert.NotEqual(windows[3].Bounds, engine.Current.Desktop.Windows.Single(w => w.Id == windows[3].Id).Bounds);
            Assert.All(engine.Current.Desktop.Windows.Where(w => windows.Take(3).Any(original => original.Id == w.Id)),
                w => { Assert.False(w.Visible); Assert.True(w.ManagerHidden); });
            Assert.True(engine.Current.Desktop.Windows.Single(w => w.Id == windows[4].Id).Visible);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndividuallyOversizedWindowDoesNotPreventItsNeighborFromTiling(bool oversizedFirst)
    {
        await using var fake = new FakeWindowPlatform();
        var ordered = fake.Desktop.Windows.OrderBy(w => w.Id).ToArray();
        var oversized = ordered[oversizedFirst ? 0 : 1] with { MinimumWidth = 1000000, MinimumHeight = 1000000 };
        var neighbor = ordered[oversizedFirst ? 1 : 0];
        fake.Desktop = fake.Desktop with { Windows = [oversized, neighbor] };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            Assert.NotEqual(oversized.Bounds, engine.Current!.Desktop.Windows.Single(w => w.Id == oversized.Id).Bounds);
            Assert.NotEqual(neighbor.Bounds, engine.Current.Desktop.Windows.Single(w => w.Id == neighbor.Id).Bounds);
            Assert.InRange(Math.Abs(engine.Current.Desktop.Windows[0].Bounds.Width - engine.Current.Desktop.Windows[1].Bounds.Width), 0, 1);
            Assert.All(engine.Current.Desktop.Windows, w => Assert.True(w.Visible && !w.ManagerHidden));
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task BothMonitorsRetileTheirOriginalWorkspacesAfterPrimaryDisplayReconnects()
    {
        await using var fake = new FakeWindowPlatform();
        var secondary = new MonitorInfo(new("00000000-0000-0000-0000-000000000001"), "Secondary",
            new(-1000, 0, 1000, 900), new(-1000, 0, 1000, 850), 96);
        var primary = new MonitorInfo(new("00000000-0000-0000-0000-000000000002"), "Primary",
            new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96);
        var mainWindow = fake.Desktop.Windows[0] with { MonitorId = primary.Id, Bounds = new(50, 50, 200, 200) };
        var otherWindow = fake.Desktop.Windows[1] with { MonitorId = secondary.Id };
        fake.Desktop = new([secondary, primary], [mainWindow, otherWindow], mainWindow.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("1"), new("2"), new("3")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready.WaitAsync(timeout.Token);
        var original = engine.Current!.Workspaces!.ToDictionary(w => w.Name);
        Assert.Equal(primary.Id, original["1"].MonitorId);
        Assert.Equal(mainWindow.Id, Assert.Single(original["1"].WindowIds));
        Assert.Equal(otherWindow.Id, Assert.Single(original["Secondary"].WindowIds));
        AssertTiled();

        fake.Desktop = fake.Desktop with { Monitors = [secondary] };
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
        fake.Desktop = fake.Desktop with { Monitors = [primary, secondary] };
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);

        Assert.Equal(original.Count, engine.Current!.Workspaces!.Count);
        foreach (var workspace in engine.Current.Workspaces)
        {
            Assert.Equal(original[workspace.Name].Id, workspace.Id);
            Assert.Equal(original[workspace.Name].MonitorId, workspace.MonitorId);
            Assert.Equal(original[workspace.Name].WindowIds, workspace.WindowIds);
            Assert.Equal(original[workspace.Name].Active, workspace.Active);
        }
        AssertTiled();
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;

        void AssertTiled()
        {
            Assert.Equal(new PixelRect(8, 8, 984, 834), engine.Current!.Desktop.Windows.Single(w => w.Id == mainWindow.Id).Bounds);
            Assert.Equal(new PixelRect(-992, 8, 984, 834), engine.Current.Desktop.Windows.Single(w => w.Id == otherWindow.Id).Bounds);
            Assert.All(engine.Current.Desktop.Windows, w => { Assert.True(w.Visible); Assert.False(w.ManagerHidden); });
        }
    }

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