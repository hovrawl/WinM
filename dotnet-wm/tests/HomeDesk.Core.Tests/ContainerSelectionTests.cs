using System.Text.Json;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class ContainerSelectionTests
{
    [Fact]
    public void TreeNavigationUsesStableNodeIdentitiesAndHandlesBoundaries()
    {
        var fake = new FakeWindowPlatform();
        var tree = new SplitTree();
        tree.Reconcile(fake.Desktop.Windows);
        var root = tree.Snapshot!;
        Assert.Equal(root.First, tree.FindNode(root.First!.Id));
        Assert.Equal(root.First, tree.NodeForWindow(root.First.WindowId!.Value));
        Assert.Equal(root, tree.Navigate(root.First.Id, ContainerNavigation.Parent));
        Assert.Equal(root.Second, tree.Navigate(root.First.Id, ContainerNavigation.Sibling));
        Assert.Equal(root.First, tree.Navigate(root.Second!.Id, ContainerNavigation.Sibling));
        Assert.Equal(root.First, tree.Navigate(root.Id, ContainerNavigation.FirstChild));
        Assert.Equal(root.Second, tree.Navigate(root.Id, ContainerNavigation.SecondChild));
        Assert.Equal(root, tree.Navigate(root.Second.Id, ContainerNavigation.Root));
        Assert.Null(tree.Navigate(root.Id, ContainerNavigation.Parent));
        Assert.Null(tree.Navigate(root.Id, ContainerNavigation.Sibling));
        Assert.Null(tree.Navigate(root.First.Id, ContainerNavigation.FirstChild));
        Assert.Null(tree.FindNode(Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Navigate(root.Id, (ContainerNavigation)99));
        Assert.False(tree.SetContainerOrientation(root.First.Id, SplitOrientation.Vertical));
        Assert.True(tree.SetContainerOrientation(root.Id, SplitOrientation.Vertical));
        tree.Reconcile(fake.Desktop.Windows);
        Assert.Equal(root.Id, tree.Snapshot!.Id);
        Assert.Equal(SplitOrientation.Vertical, tree.Snapshot.Orientation);
    }

    [Fact]
    public async Task SelectionNavigatesWithoutNativeFocusAndChangesTheSelectedSplit()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = Assert.Single(engine.Current!.Trees).Value!;
        var sink = new Sink();
        await engine.SubscribeAsync(sink, [ManagerEventTypes.SelectionChanged], timeout.Token);
        var result = await engine.ExecuteAsync(new(CommandKind.SelectContainer, root.First!.WindowId), timeout.Token);
        Assert.True(result.Success);
        Assert.Equal(root.First.Id, engine.Current.Selection!.NodeId);
        Assert.Null(engine.Current.Desktop.FocusedWindowId);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SelectContainer, Navigation: ContainerNavigation.Parent), timeout.Token)).Success);
        Assert.Equal(root.Id, engine.Current.Selection!.NodeId);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SetSplit, Orientation: SplitOrientation.Vertical), timeout.Token)).Success);
        Assert.Equal(SplitOrientation.Vertical, Assert.Single(engine.Current.Trees).Value!.Orientation);
        Assert.Equal(root.Id, engine.Current.Selection!.NodeId);
        Assert.Equal(new[] { ManagerEventTypes.Snapshot, ManagerEventTypes.SelectionChanged, ManagerEventTypes.SelectionChanged },
            sink.Events.Select(envelope => envelope.EventType));
        Assert.Equal(root.Id, sink.Events[^1].Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Selection!.NodeId);
        Assert.True(sink.Events[2].StateRevision > sink.Events[1].StateRevision);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.ClearSelection), timeout.Token)).Success);
        Assert.Null(engine.Current.Selection);
        var revision = engine.Current.StateRevision;
        await engine.ExecuteAsync(new(CommandKind.ClearSelection), timeout.Token);
        Assert.Equal(revision, engine.Current.StateRevision);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task InvalidSelectionAndLeafSplitReturnErrorsWithoutMutation()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = Assert.Single(engine.Current!.Trees).Value!;
        await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: root.Id), timeout.Token);
        var revision = engine.Current.StateRevision;
        var invalid = new ManagerCommand[]
        {
            new(CommandKind.SelectContainer, NodeId: root.Id, Navigation: ContainerNavigation.Parent),
            new(CommandKind.SelectContainer, fake.Desktop.Windows[0].Id, NodeId: root.Id),
            new(CommandKind.SelectContainer, Navigation: (ContainerNavigation)99),
            new(CommandKind.Redraw, NodeId: root.Id)
        };
        foreach (var command in invalid)
            Assert.Equal("invalid_argument", (await engine.ExecuteAsync(command, timeout.Token)).ErrorCode);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: Guid.NewGuid()), timeout.Token)).ErrorCode);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.SelectContainer, Navigation: ContainerNavigation.Parent), timeout.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current.StateRevision);
        Assert.Equal(root.Id, engine.Current.Selection!.NodeId);
        await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: root.First!.Id), timeout.Token);
        revision = engine.Current.StateRevision;
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.SetSplit, Orientation: SplitOrientation.Vertical), timeout.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current.StateRevision);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("remove")]
    [InlineData("float")]
    [InlineData("workspace")]
    public async Task SelectionClearsWhenItsContextDisappears(string change)
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("1"), new("2")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = engine.Current!.Trees.Values.First(node => node is not null)!;
        var windowId = root.First!.WindowId!.Value;
        await engine.ExecuteAsync(new(CommandKind.Focus, windowId), timeout.Token);
        await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: root.First.Id), timeout.Token);
        Assert.NotNull(engine.Current.Selection);
        switch (change)
        {
            case "focus":
                fake.Desktop = fake.Desktop with { FocusedWindowId = root.Second!.WindowId };
                await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
                break;
            case "remove":
                fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Where(window => window.Id != windowId).ToArray() };
                await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
                break;
            case "float":
                await engine.ExecuteAsync(new(CommandKind.SetMode, windowId, Mode: WindowMode.Floating), timeout.Token);
                break;
            case "workspace":
                await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "2"), timeout.Token);
                break;
        }
        Assert.Null(engine.Current.Selection);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task ExplicitWindowSplitBypassesSelectionAndFailedFocusPreservesIt()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = Assert.Single(engine.Current!.Trees).Value!;
        await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: root.First!.Id), timeout.Token);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SetSplit, root.First.WindowId, Orientation: SplitOrientation.Vertical), timeout.Token)).Success);
        fake.FailActions = true;
        Assert.False((await engine.ExecuteAsync(new(CommandKind.Focus, root.Second!.WindowId), timeout.Token)).Success);
        Assert.Equal(root.First.Id, engine.Current.Selection!.NodeId);
        fake.FailActions = false;
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task SelectedAncestorIsOrientedInsteadOfTheFocusedTilesParent()
    {
        await using var fake = new FakeWindowPlatform();
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Append(fake.Desktop.Windows[0] with { Id = Guid.NewGuid() }).ToArray() };
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        var root = Assert.Single(engine.Current!.Trees).Value!;
        var nested = root.Second!;
        await engine.ExecuteAsync(new(CommandKind.Focus, nested.Second!.WindowId), timeout.Token);
        await engine.ExecuteAsync(new(CommandKind.SelectContainer, Navigation: ContainerNavigation.Root), timeout.Token);
        Assert.Equal(root.Id, engine.Current.Selection!.NodeId);
        await engine.ExecuteAsync(new(CommandKind.SetSplit, Orientation: SplitOrientation.Vertical), timeout.Token);
        var changed = Assert.Single(engine.Current.Trees).Value!;
        Assert.Equal(SplitOrientation.Vertical, changed.Orientation);
        Assert.Equal(SplitOrientation.Horizontal, changed.Second!.Orientation);
        await engine.ExecuteAsync(new(CommandKind.SetSplit, nested.Second.WindowId, Orientation: SplitOrientation.Vertical), timeout.Token);
        Assert.Equal(SplitOrientation.Vertical, Assert.Single(engine.Current.Trees).Value!.Second!.Orientation);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task InactiveWorkspaceAndUnfocusedOrFloatingTargetsCannotBeSelected()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake, workspaceDefinitions: [new("1"), new("2")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        await engine.Ready;
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.SelectContainer), timeout.Token)).ErrorCode);
        var root = engine.Current!.Trees.Values.First(node => node is not null)!;
        var windowId = root.First!.WindowId!.Value;
        await engine.ExecuteAsync(new(CommandKind.SetMode, windowId, Mode: WindowMode.Floating), timeout.Token);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.SelectContainer, windowId), timeout.Token)).ErrorCode);
        var retained = engine.Current.Trees.Values.First(node => node is not null)!.Id;
        await engine.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "2"), timeout.Token);
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.SelectContainer, NodeId: retained), timeout.Token)).ErrorCode);
        Assert.Null(engine.Current.Selection);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    private sealed class Sink : IManagerEventSink
    {
        public List<EventEnvelope> Events { get; } = [];
        public bool TryPublish(EventEnvelope envelope) { Events.Add(envelope); return true; }
        public void Disconnect(string reason) { }
    }
}