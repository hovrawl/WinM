using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class FocusCyclingTests
{
    [Theory]
    [InlineData(FocusCycleDirection.Next)]
    [InlineData(FocusCycleDirection.Previous)]
    public async Task CyclingStartsWithoutFocusFollowsTreeOrderAndWraps(FocusCycleDirection direction)
    {
        await using var fake = new FakeWindowPlatform();
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Append(fake.Desktop.Windows[0] with { Id = Guid.NewGuid() }).ToArray() };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var tree = new SplitTree();
        tree.Reconcile(engine.Current!.Desktop.Windows);
        var expected = direction == FocusCycleDirection.Next ? tree.WindowOrder.ToArray() : tree.WindowOrder.Reverse().ToArray();
        foreach (var id in expected.Append(expected[0]))
        {
            Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle, CycleDirection: direction), timeout.Token)).Success);
            Assert.Equal(id, engine.Current.Desktop.FocusedWindowId);
        }
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Theory]
    [InlineData(WindowMode.Tiling)]
    [InlineData(WindowMode.Floating)]
    public async Task FiltersSelectOnlyTheRequestedModeAndSingleMatchIsANoop(WindowMode mode)
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var floating = fake.Desktop.Windows[0].Id;
        var tiling = fake.Desktop.Windows[1].Id;
        await engine.ExecuteAsync(new(CommandKind.SetMode, floating, Mode: WindowMode.Floating), timeout.Token);
        var expected = mode == WindowMode.Floating ? floating : tiling;
        var other = mode == WindowMode.Floating ? tiling : floating;
        await engine.ExecuteAsync(new(CommandKind.Focus, other), timeout.Token);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle, Mode: mode), timeout.Token)).Success);
        Assert.Equal(expected, engine.Current!.Desktop.FocusedWindowId);
        var revision = engine.Current.StateRevision;
        fake.Plans.Clear();
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle, Mode: mode, CycleDirection: FocusCycleDirection.Previous), timeout.Token)).Success);
        Assert.Equal(revision, engine.Current.StateRevision);
        Assert.Empty(fake.Plans);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task UnfilteredCycleVisitsTilesThenFloatingWindowsAndRespectsTileReordering()
    {
        await using var fake = new FakeWindowPlatform();
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Append(fake.Desktop.Windows[0] with { Id = Guid.NewGuid() }).ToArray() };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var floating = fake.Desktop.Windows[0].Id;
        await engine.ExecuteAsync(new(CommandKind.SetMode, floating, Mode: WindowMode.Floating), timeout.Token);
        var root = Assert.Single(engine.Current!.Trees).Value!;
        var first = root.First!.WindowId!.Value;
        var second = root.Second!.WindowId!.Value;
        await engine.ExecuteAsync(new(CommandKind.Focus, first), timeout.Token);
        await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token);
        Assert.Equal(second, engine.Current.Desktop.FocusedWindowId);
        await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token);
        Assert.Equal(floating, engine.Current.Desktop.FocusedWindowId);
        await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token);
        Assert.Equal(first, engine.Current.Desktop.FocusedWindowId);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Move, first, Direction.Right), timeout.Token)).Success);
        await engine.ExecuteAsync(new(CommandKind.FocusCycle, Mode: WindowMode.Tiling), timeout.Token);
        Assert.Equal(second, engine.Current.Desktop.FocusedWindowId);
        var swapped = Assert.Single(engine.Current.Trees).Value!;
        Assert.Equal(second, swapped.First!.WindowId);
        Assert.Equal(first, swapped.Second!.WindowId);
        fake.Desktop = fake.Desktop with { FocusedWindowId = null };
        await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
        await engine.ExecuteAsync(new(CommandKind.FocusCycle, Mode: WindowMode.Tiling), timeout.Token);
        Assert.Equal(second, engine.Current.Desktop.FocusedWindowId);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Theory]
    [InlineData("minimized")]
    [InlineData("invisible")]
    [InlineData("popup")]
    [InlineData("fullscreen")]
    [InlineData("noActivate")]
    public async Task UnfocusableWindowsAreExcluded(string reason)
    {
        await using var fake = new FakeWindowPlatform();
        var original = fake.Desktop.Windows[0];
        var excluded = reason switch
        {
            "minimized" => original with { Minimized = true },
            "invisible" => original with { Visible = false },
            "popup" => original with { IsPopup = true },
            "fullscreen" => original with { Fullscreen = true },
            _ => original with { IsNoActivate = true }
        };
        fake.Desktop = fake.Desktop with { Windows = [excluded, fake.Desktop.Windows[1]] };
        var expected = fake.Desktop.Windows[1].Id;
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token)).Success);
        Assert.Equal(expected, engine.Current!.Desktop.FocusedWindowId);
        var revision = engine.Current.StateRevision;
        await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token);
        Assert.Equal(revision, engine.Current.StateRevision);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task WorkspaceScopeExcludesHiddenWindowsAndDoesNotActivateAnInactiveWorkspace()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("1"), new("2")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var hidden = fake.Desktop.Windows[0].Id;
        var visible = fake.Desktop.Windows[1].Id;
        await engine.ExecuteAsync(new(CommandKind.MoveToWorkspace, hidden, WorkspaceName: "2"), timeout.Token);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle, WorkspaceName: "1"), timeout.Token)).Success);
        Assert.Equal(visible, engine.Current!.Desktop.FocusedWindowId);
        var revision = engine.Current.StateRevision;
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.FocusCycle, WorkspaceName: "2"), timeout.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current.StateRevision);
        Assert.True(engine.Current.Workspaces!.Single(workspace => workspace.Name == "1").Active);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task ExplicitAnchorCyclesFromThatWindowAndInvalidArgumentsDoNotMutate()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = Assert.Single(engine.Current!.Trees).Value!;
        await engine.ExecuteAsync(new(CommandKind.FocusCycle, root.First!.WindowId), timeout.Token);
        Assert.Equal(root.Second!.WindowId, engine.Current.Desktop.FocusedWindowId);
        var revision = engine.Current.StateRevision;
        var invalid = new ManagerCommand[]
        {
            new(CommandKind.FocusCycle, CycleDirection: (FocusCycleDirection)99),
            new(CommandKind.FocusCycle, Mode: (WindowMode)99),
            new(CommandKind.FocusCycle, Direction: Direction.Left),
            new(CommandKind.FocusCycle, WorkspaceId: Guid.NewGuid(), WorkspaceName: "1"),
            new(CommandKind.Redraw, CycleDirection: FocusCycleDirection.Next)
        };
        foreach (var command in invalid)
            Assert.Equal("invalid_argument", (await engine.ExecuteAsync(command, timeout.Token)).ErrorCode);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.FocusCycle, Guid.NewGuid()), timeout.Token)).ErrorCode);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.FocusCycle, Mode: WindowMode.Floating), timeout.Token)).ErrorCode);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.FocusCycle, WorkspaceId: Guid.NewGuid()), timeout.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current.StateRevision);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task FailedNativeFocusPreservesObservedFocusAndSelection()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = Assert.Single(engine.Current!.Trees).Value!;
        await engine.ExecuteAsync(new(CommandKind.Focus, root.First!.WindowId), timeout.Token);
        await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: root.Id), timeout.Token);
        var revision = engine.Current.StateRevision;
        fake.FailActions = true;
        Assert.Equal("access_denied", (await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token)).ErrorCode);
        Assert.Equal(root.First.WindowId, engine.Current.Desktop.FocusedWindowId);
        Assert.Equal(root.Id, engine.Current.Selection!.NodeId);
        Assert.Equal(revision, engine.Current.StateRevision);
        fake.FailActions = false;
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token)).Success);
        Assert.Equal(root.Second!.WindowId, engine.Current.Desktop.FocusedWindowId);
        Assert.Null(engine.Current.Selection);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task EmptyDesktopHasNoCycleTarget()
    {
        await using var fake = new FakeWindowPlatform();
        fake.Desktop = new([], [], null);
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var revision = engine.Current!.StateRevision;
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current.StateRevision);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task MultipleMonitorsDefaultToPrimaryAndSupportExplicitActiveWorkspaceScope()
    {
        await using var fake = new FakeWindowPlatform();
        var primaryId = Guid.NewGuid();
        var primary = new MonitorInfo(primaryId, "primary", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96);
        var primaryWindow = fake.Desktop.Windows[0] with { Id = Guid.NewGuid(), MonitorId = primaryId, Bounds = new(100, 100, 200, 200) };
        fake.Desktop = fake.Desktop with
        {
            Monitors = fake.Desktop.Monitors.Append(primary).ToArray(),
            Windows = fake.Desktop.Windows.Append(primaryWindow).ToArray()
        };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle), timeout.Token)).Success);
        Assert.Equal(primaryWindow.Id, engine.Current!.Desktop.FocusedWindowId);
        var other = engine.Current.Workspaces!.Single(workspace => workspace.MonitorId != primaryId);
        var expected = engine.Current.Trees[other.Id]!.First!.WindowId;
        Assert.True((await engine.ExecuteAsync(new(CommandKind.FocusCycle, WorkspaceId: other.Id), timeout.Token)).Success);
        Assert.Equal(expected, engine.Current.Desktop.FocusedWindowId);
        var revision = engine.Current.StateRevision;
        Assert.Equal("invalid_argument", (await engine.ExecuteAsync(new(CommandKind.FocusCycle, primaryWindow.Id, WorkspaceId: other.Id), timeout.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current.StateRevision);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }
}