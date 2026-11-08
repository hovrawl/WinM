using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class WorkspaceManagerTests
{
    [Fact]
    public async Task OversizedWindowIsKeptManagedButNotStagedAlongsideWindowsThatFit()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 800, 600), new(0, 0, 800, 600), 96);
        var template = fake.Desktop.Windows[0];
        var oversized = template with
        {
            Id = new(1, 0, 0, new byte[8]), MonitorId = monitor.Id, MinimumWidth = 1200, MinimumHeight = 100
        };
        var fitting = template with
        {
            Id = new(2, 0, 0, new byte[8]), MonitorId = monitor.Id, MinimumWidth = 100, MinimumHeight = 100
        };
        fake.Desktop = new([monitor], [oversized, fitting], fitting.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);

            var snapshot = engine.Current!;
            Assert.Equal(2, snapshot.Workspaces!.Single().WindowIds.Count);
            Assert.True(snapshot.Desktop.Windows.Single(window => window.Id == oversized.Id).Minimized);
            var staged = snapshot.Desktop.Windows.Single(window => window.Id == fitting.Id);
            Assert.False(staged.Minimized);
            Assert.True(staged.Bounds.Width >= fitting.MinimumWidth);
            Assert.True(staged.Bounds.Height >= fitting.MinimumHeight);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
        Assert.All(fake.Desktop.Windows, window =>
        {
            Assert.False(window.Minimized);
            Assert.True(window.Visible);
            Assert.False(window.ManagerHidden);
        });
    }

    [Fact]
    public async Task PrimaryMinimumSizeOverflowDoesNotBlockAllTilingAndRecoversWhenSpaceReturns()
    {
        await using var fake = new FakeWindowPlatform();
        var primary = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 1920, 1200), new(0, 0, 1920, 1200), 144);
        var secondary = primary with { Id = Guid.NewGuid(), DeviceName = "Secondary", Bounds = new(-1920, 0, 1920, 1200), WorkArea = new(-1920, 0, 1920, 1200) };
        var template = fake.Desktop.Windows[0];
        WindowInfo[] windows =
        [
            template with { Id = new(1, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 1200, MinimumHeight = 500 },
            template with { Id = new(2, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 1200, MinimumHeight = 500 },
            template with { Id = new(3, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 1200, MinimumHeight = 500 },
            template with { Id = new(4, 0, 0, new byte[8]), MonitorId = primary.Id, MinimumWidth = 1200, MinimumHeight = 500 },
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
            Assert.All(windows.Skip(1).Take(3), window =>
            {
                var overflow = current.Desktop.Windows.Single(observed => observed.Id == window.Id);
                Assert.True(overflow.LayoutOverflow && overflow.Minimized);
            });
            Assert.NotEqual(windows[4].Bounds, current.Desktop.Windows.Single(w => w.Id == windows[4].Id).Bounds);
            Assert.All(current.Desktop.Windows, w => { Assert.True(w.Visible); Assert.False(w.ManagerHidden); Assert.Equal(WindowMode.Tiling, w.Mode); });
            Assert.Equal(4, current.Workspaces!.Single(w => w.Name == "1").WindowIds.Count);
            var result = await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
            Assert.True(result.Success);
            Assert.Empty(diagnostics);

            fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(w => w with { MinimumWidth = 100, MinimumHeight = 100 }).ToArray() };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.All(engine.Current!.Desktop.Windows.Where(window => windows.Take(4).Any(original => original.Id == window.Id)),
                window => Assert.False(window.Minimized || window.LayoutOverflow));
            fake.Desktop = fake.Desktop with
            {
                Monitors = [primary with { WorkArea = new(0, 0, 5000, 1200) }, secondary],
                Windows = windows
            };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.All(engine.Current!.Desktop.Windows.Where(window => windows.Take(4).Any(original => original.Id == window.Id)),
                window => Assert.False(window.Minimized || window.LayoutOverflow));
            fake.Desktop = fake.Desktop with { Monitors = [primary, secondary], Windows = windows };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.Empty(diagnostics);

            var destination = engine.Current!.Workspaces!.Single(w => w.Name == "2");
            Assert.True((await engine.ExecuteAsync(new(CommandKind.MoveToWorkspace, windows[3].Id, WorkspaceId: destination.Id), timeout.Token)).Success);
            Assert.Equal(2, engine.Current!.Workspaces!.Single(w => w.Id == destination.Id).WindowIds.Count);
            Assert.True((await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: destination.Id), timeout.Token)).Success);
            Assert.True(engine.Current!.Workspaces!.Single(w => w.Id == destination.Id).Active);
            Assert.True(engine.Current.Desktop.Windows.Single(w => w.Id == windows[3].Id).Visible);
            Assert.NotEqual(windows[3].Bounds, engine.Current.Desktop.Windows.Single(w => w.Id == windows[3].Id).Bounds);
            var hidden = engine.Current.Desktop.Windows.Single(window => window.Id == windows[0].Id);
            Assert.False(hidden.Visible);
            Assert.True(hidden.ManagerHidden);
            Assert.All(engine.Current.Desktop.Windows.Where(window => windows.Skip(1).Take(2).Any(original => original.Id == window.Id)),
                window => Assert.True(window.Minimized && window.LayoutOverflow));
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
            var current = engine.Current!;
            var overflow = current.Desktop.Windows.Single(window => window.Id == oversized.Id);
            var staged = current.Desktop.Windows.Single(window => window.Id == neighbor.Id);
            Assert.True(overflow.LayoutOverflow && overflow.Minimized);
            Assert.False(staged.LayoutOverflow || staged.Minimized);
            Assert.NotEqual(neighbor.Bounds, staged.Bounds);
            Assert.True(staged.Bounds.Width >= neighbor.MinimumWidth);
            Assert.True(staged.Bounds.Height >= neighbor.MinimumHeight);
            Assert.All(current.Desktop.Windows, window => Assert.True(window.Visible && !window.ManagerHidden));
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task FocusingOverflowWindowSwapsItWithTheFocusedTileAndExpandedSpaceRestoresBoth()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 1000, 800), new(0, 0, 1000, 800), 96);
        var template = fake.Desktop.Windows[0];
        var first = template with { Id = new(1, 0, 0, new byte[8]), MonitorId = monitor.Id, MinimumWidth = 600 };
        var second = template with { Id = new(2, 0, 0, new byte[8]), MonitorId = monitor.Id, MinimumWidth = 600 };
        fake.Desktop = new([monitor], [first, second], first.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var before = engine.Current!;
            Assert.True(before.Desktop.Windows.Single(window => window.Id == second.Id).LayoutOverflow);
            Assert.True(before.Desktop.Windows.Single(window => window.Id == second.Id).Minimized);

            var restore = await engine.ExecuteAsync(new(CommandKind.Focus, second.Id), timeout.Token);

            Assert.True(restore.Success);
            var restored = engine.Current!;
            Assert.Equal(second.Id, restored.Desktop.FocusedWindowId);
            Assert.False(restored.Desktop.Windows.Single(window => window.Id == second.Id).Minimized);
            Assert.False(restored.Desktop.Windows.Single(window => window.Id == second.Id).LayoutOverflow);
            Assert.True(restored.Desktop.Windows.Single(window => window.Id == first.Id).Minimized);
            Assert.True(restored.Desktop.Windows.Single(window => window.Id == first.Id).LayoutOverflow);
            Assert.Equal(2, restored.Workspaces!.Single().WindowIds.Count);

            fake.Desktop = fake.Desktop with { Monitors = [monitor with { WorkArea = new(0, 0, 1400, 800) }] };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            Assert.All(engine.Current!.Desktop.Windows, window => Assert.False(window.Minimized || window.LayoutOverflow));
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task ApplicationMinimizedWindowIsNotMarkedAsOverflowOrAutoRestored()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = fake.Desktop.Monitors[0];
        var windows = fake.Desktop.Windows.OrderBy(window => window.Id).ToArray();
        var manuallyMinimized = windows[0] with { Minimized = true };
        fake.Desktop = fake.Desktop with { Windows = [manuallyMinimized, windows[1]] };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var window = engine.Current!.Desktop.Windows.Single(candidate => candidate.Id == manuallyMinimized.Id);
            Assert.True(window.Minimized);
            Assert.False(window.LayoutOverflow);

            fake.Desktop = fake.Desktop with { Monitors = [monitor with { WorkArea = new(0, 0, 2000, 1200) }] };
            Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token)).Success);
            window = engine.Current!.Desktop.Windows.Single(candidate => candidate.Id == manuallyMinimized.Id);
            Assert.True(window.Minimized);
            Assert.False(window.LayoutOverflow);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task RejectedOverflowMinimizationRemainsExplicitAndRetryable()
    {
        await using var fake = new FakeWindowPlatform();
        var monitor = fake.Desktop.Monitors[0];
        var windows = fake.Desktop.Windows.OrderBy(window => window.Id).ToArray();
        var oversized = windows[0] with { MinimumWidth = 1000000, MinimumHeight = 1000000 };
        fake.Desktop = fake.Desktop with { Windows = [oversized, windows[1]] };
        fake.FailActions = true;
        var engine = new ManagerEngine(fake);
        var failures = new List<IReadOnlyList<NativeActionFailure>>();
        engine.ActionsFailed += failures.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var overflow = engine.Current!.Desktop.Windows.Single(window => window.Id == oversized.Id);
            Assert.True(overflow.LayoutOverflow);
            Assert.False(overflow.Minimized);
            Assert.Contains(failures.SelectMany(items => items), failure => failure.Code == "access_denied");
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
        Assert.Equal(otherWindow.Id, Assert.Single(original["2"].WindowIds));
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

    [Fact]
    public async Task ActivateWorkspaceTransfersItsViewToRequestedMonitorAndUsesFocusedMonitorByDefault()
    {
        await using var fake = new FakeWindowPlatform();
        var primary = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96);
        var secondary = new MonitorInfo(Guid.NewGuid(), "Secondary", new(1000, 0, 1000, 900), new(1000, 0, 1000, 850), 96);
        var window = fake.Desktop.Windows[0] with { MonitorId = primary.Id, Bounds = new(50, 50, 300, 300) };
        fake.Desktop = new([primary, secondary], [window], window.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One"), new("Two"), new("Three")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var one = engine.Current!.Workspaces!.Single(workspace => workspace.Name == "One");
            var two = engine.Current.Workspaces!.Single(workspace => workspace.Name == "Two");
            var three = engine.Current.Workspaces!.Single(workspace => workspace.Name == "Three");
            var membership = engine.Current.Workspaces!.ToDictionary(workspace => workspace.Id,
                workspace => workspace.WindowIds.ToArray());
            var originalViews = engine.Current.WorkspaceViews!.ToArray();

            var invalidTarget = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace,
                WorkspaceId: one.Id, MonitorId: Guid.NewGuid()), timeout.Token);

            Assert.False(invalidTarget.Success);
            Assert.Equal("not_found", invalidTarget.ErrorCode);
            Assert.Equal(originalViews, engine.Current.WorkspaceViews!);

            var transfer = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace,
                WorkspaceId: one.Id, MonitorId: secondary.Id), timeout.Token);

            Assert.True(transfer.Success);
            Assert.Equal(one.Id, engine.Current!.WorkspaceViews!.Single(view => view.MonitorId == secondary.Id).WorkspaceId);
            Assert.Equal(three.Id, engine.Current.WorkspaceViews!.Single(view => view.MonitorId == primary.Id).WorkspaceId);
            Assert.DoesNotContain(engine.Current.WorkspaceViews!, view => view.WorkspaceId == two.Id);
            Assert.All(engine.Current.Workspaces!, workspace => Assert.Equal(membership[workspace.Id], workspace.WindowIds));
            Assert.True(engine.Current.Desktop.Windows.Single(candidate => candidate.Id == window.Id).Visible);
            Assert.InRange(engine.Current.Desktop.Windows.Single(candidate => candidate.Id == window.Id).Bounds.X,
                secondary.WorkArea.X, secondary.WorkArea.X + secondary.WorkArea.Width);

            var focusedDefault = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: two.Id), timeout.Token);

            Assert.True(focusedDefault.Success);
            Assert.Equal(two.Id, engine.Current!.WorkspaceViews!.Single(view => view.MonitorId == primary.Id).WorkspaceId);
            Assert.Equal(one.Id, engine.Current.Workspaces!.Single(workspace => workspace.Name == "One").Id);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task FailedCrossMonitorTransferRestoresViewsAndRemovesCreatedFallback()
    {
        await using var fake = new FakeWindowPlatform();
        var primary = new MonitorInfo(Guid.NewGuid(), "Primary", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96);
        var secondary = new MonitorInfo(Guid.NewGuid(), "Secondary", new(1000, 0, 1000, 900), new(1000, 0, 1000, 850), 96);
        var window = fake.Desktop.Windows[0] with { MonitorId = primary.Id };
        fake.Desktop = new([primary, secondary], [window], window.Id);
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One"), new("Two")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var before = engine.Current!;
            var source = before.Workspaces!.Single(workspace => workspace.Name == "One");
            var ids = before.Workspaces!.Select(workspace => workspace.Id).ToHashSet();
            fake.FailActions = true;

            var result = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace,
                WorkspaceId: source.Id, MonitorId: secondary.Id), timeout.Token);

            Assert.False(result.Success);
            Assert.Equal(before.WorkspaceViews, engine.Current!.WorkspaceViews);
            Assert.Equal(ids, engine.Current.Workspaces!.Select(workspace => workspace.Id).ToHashSet());
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }

    [Fact]
    public async Task WorkspaceActivationWithoutConnectedMonitorsReturnsNotReady()
    {
        await using var fake = new FakeWindowPlatform { Desktop = new([], [], null) };
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("One")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await engine.Ready.WaitAsync(timeout.Token);
            var workspace = engine.Current!.Workspaces!.Single();
            var result = await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceId: workspace.Id), timeout.Token);
            Assert.False(result.Success);
            Assert.Equal("not_ready", result.ErrorCode);
            Assert.Empty(engine.Current.WorkspaceViews!);
        }
        finally
        {
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
    }
}