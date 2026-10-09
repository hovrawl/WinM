using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class WorkspaceTests
{
    private static readonly Guid FirstMonitor = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondMonitor = new("00000000-0000-0000-0000-000000000002");
    private static MonitorInfo Monitor(Guid id, string name = "Display", uint dpi = 96) =>
        new(id, name, new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040), dpi);
    private static WindowInfo Window(Guid? id = null, Guid? monitor = null) =>
        new(id ?? Guid.NewGuid(), monitor ?? FirstMonitor, "Window", "test", "test",
            new(-1920, 0, 800, 600), true, false, false, WindowMode.Tiling);
    private static DesktopSnapshot Desktop(WindowInfo[] windows, params MonitorInfo[] monitors) => new(monitors, windows, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnboundConfiguredWorkspacesStartOnPrimaryMonitorRegardlessOfGuidOrder(bool reverse)
    {
        var primary = Monitor(SecondMonitor, "Primary") with { Bounds = new(0, 0, 1920, 1080), WorkArea = new(0, 0, 1920, 1040) };
        var secondary = Monitor(FirstMonitor, "Secondary");
        var set = new WorkspaceSet([new("1"), new("2"), new("3")]);
        set.Reconcile(Desktop([], reverse ? [primary, secondary] : [secondary, primary]));

        Assert.All(set.Snapshot.Where(w => w.Name is "1" or "2" or "3"), w => Assert.Equal(primary.Id, w.MonitorId));
        Assert.True(set.Snapshot.Single(w => w.Name == "1").Active);
        Assert.True(set.Snapshot.Single(w => w.Name == "Secondary").Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticWorkspaceReturnsToReconnectedDisplayWithoutDuplicates(bool newMonitorId)
    {
        var first = Window();
        var second = Window(monitor: SecondMonitor);
        var set = new WorkspaceSet();
        var desktop = Desktop([first, second], Monitor(FirstMonitor, "Left"), Monitor(SecondMonitor, "Right"));
        set.Reconcile(desktop);
        var workspace = set.Snapshot.Single(w => w.MonitorId == SecondMonitor);
        var tree = set.Trees[workspace.Id];
        set.Reconcile(desktop with { Monitors = [Monitor(FirstMonitor, "Left")] });

        var reconnected = newMonitorId ? Guid.NewGuid() : SecondMonitor;
        set.Reconcile(Desktop([first, second with { MonitorId = reconnected }],
            Monitor(FirstMonitor, "Left"), Monitor(reconnected, "right")));

        Assert.Equal(2, set.Snapshot.Count);
        var restored = set.Snapshot.Single(w => w.Id == workspace.Id);
        Assert.Equal(reconnected, restored.MonitorId);
        Assert.True(restored.Active);
        Assert.Equal(second.Id, Assert.Single(restored.WindowIds));
        Assert.Same(tree, set.Trees[workspace.Id]);
        Assert.Equal(second.Id, tree.Snapshot!.WindowId);
        Assert.Empty(set.Visibility(desktop));
    }

    [Fact]
    public void DefaultsAssignWindowsToTheirPhysicalMonitorAndCreateWorkspaceKeyedTrees()
    {
        var first = Window();
        var second = Window(monitor: SecondMonitor);
        var set = new WorkspaceSet();
        set.Reconcile(Desktop([first, second], Monitor(SecondMonitor, "Right"), Monitor(FirstMonitor, "Left")));
        Assert.Equal(2, set.Snapshot.Count);
        foreach (var workspace in set.Snapshot)
        {
            Assert.True(workspace.Active);
            Assert.Equal(workspace.Id, set.WorkspaceFor(workspace.WindowIds.Single()));
            Assert.Equal(workspace.WindowIds.Single(), set.Trees[workspace.Id].Snapshot!.WindowId);
        }
        Assert.Equal(FirstMonitor, set.Snapshot.Single(w => w.Id == set.WorkspaceFor(first.Id)).MonitorId);
        Assert.Equal(SecondMonitor, set.Snapshot.Single(w => w.Id == set.WorkspaceFor(second.Id)).MonitorId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new WorkspaceSet([new(name)]));

    [Fact]
    public void DuplicateNamesAreRejectedIgnoringCase() =>
        Assert.Throws<ArgumentException>(() => new WorkspaceSet([new("Work"), new("work")]));

    [Fact]
    public void ConfiguredDeviceNamesResolveAndEveryMonitorHasOneActiveWorkspace()
    {
        var set = new WorkspaceSet([new("Code", "left"), new("Chat", "Left"), new("Media", "Right")]);
        set.Reconcile(Desktop([], Monitor(FirstMonitor, "Left"), Monitor(SecondMonitor, "Right")));
        Assert.Equal(FirstMonitor, set.Snapshot.Single(w => w.Name == "Code").MonitorId);
        Assert.Equal(SecondMonitor, set.Snapshot.Single(w => w.Name == "Media").MonitorId);
        Assert.All(set.Snapshot.GroupBy(w => w.MonitorId), g => Assert.Single(g, w => w.Active));
        var chat = set.Snapshot.Single(w => w.Name == "Chat");
        Assert.True(set.Activate(chat.Id));
        Assert.False(set.Activate(chat.Id));
        Assert.True(set.Snapshot.Single(w => w.Name == "Media").Active);
        Assert.False(set.Activate(Guid.NewGuid()));
    }

    [Fact]
    public void ManagerHiddenWindowsKeepInactiveTreesAndRatiosAcrossTransitions()
    {
        var first = Window();
        var second = Window();
        var set = new WorkspaceSet([new("One"), new("Two")]);
        set.Reconcile(Desktop([first, second], Monitor(FirstMonitor)));
        var one = set.Snapshot[0].Id;
        var two = set.Snapshot[1].Id;
        var tree = set.Trees[one];
        Assert.True(tree.Resize(first.Id, Direction.Right, .2));
        var before = tree.Snapshot;
        Assert.True(set.Activate(two));
        Assert.Equal(2, set.Visibility(Desktop([first, second], Monitor(FirstMonitor))).Count);
        var hidden = new[] { first with { Visible = false, ManagerHidden = true }, second with { Visible = false, ManagerHidden = true } };
        set.Reconcile(Desktop(hidden, Monitor(FirstMonitor)));
        Assert.Equal(before, tree.Snapshot);
        Assert.Same(tree, set.Trees[one]);
        Assert.Empty(set.Visibility(Desktop(hidden, Monitor(FirstMonitor))));
        Assert.True(set.Activate(one));
        Assert.All(set.Visibility(Desktop(hidden, Monitor(FirstMonitor))), v => Assert.True(v.Visible));
        Assert.Equal(2, set.Visibility(Desktop(hidden, Monitor(FirstMonitor))).Count);
    }

    [Fact]
    public void MoveToMonitorSurvivesStaleGeometryAndPhysicalMonitorRecapture()
    {
        var window = Window();
        var set = new WorkspaceSet();
        var desktop = Desktop([window], Monitor(FirstMonitor), Monitor(SecondMonitor, "Right"));
        set.Reconcile(desktop);
        var source = set.WorkspaceFor(window.Id)!.Value;
        Assert.True(set.MoveWindowToMonitor(window.Id, SecondMonitor));
        var target = set.WorkspaceFor(window.Id);
        Assert.Null(set.Trees[source].Snapshot);
        set.Reconcile(desktop);
        Assert.Equal(target, set.WorkspaceFor(window.Id));
        Assert.Equal(SecondMonitor, set.Snapshot.Single(w => w.Id == target).MonitorId);
        set.Reconcile(desktop with { Windows = [window with { MonitorId = SecondMonitor }] });
        Assert.Equal(target, set.WorkspaceFor(window.Id));
        Assert.False(set.MoveWindowToMonitor(window.Id, SecondMonitor));
        Assert.False(set.MoveWindowToMonitor(window.Id, Guid.NewGuid()));
    }

    [Fact]
    public void MovesPreserveUniqueOwnershipAndRemovedWindowsArePruned()
    {
        var window = Window();
        var set = new WorkspaceSet([new("One"), new("Two")]);
        set.Reconcile(Desktop([window], Monitor(FirstMonitor)));
        var target = set.Snapshot[1].Id;
        Assert.True(set.MoveWindow(window.Id, target));
        Assert.False(set.MoveWindow(window.Id, target));
        Assert.False(set.MoveWindow(Guid.NewGuid(), target));
        Assert.False(set.MoveWindow(window.Id, Guid.NewGuid()));
        Assert.Single(set.Snapshot.SelectMany(w => w.WindowIds));
        Assert.Equal(target, set.WorkspaceFor(window.Id));
        Assert.Equal(new WindowVisibility(window.Id, false), Assert.Single(set.Visibility(Desktop([window]))));
        set.Reconcile(Desktop([], Monitor(FirstMonitor)));
        Assert.Null(set.WorkspaceFor(window.Id));
        Assert.All(set.Trees.Values, t => Assert.Null(t.Snapshot));
    }

    [Fact]
    public void MonitorRemovalMigratesWorkspacesWithoutLosingIdentityOrAssignments()
    {
        var window = Window(monitor: SecondMonitor);
        var set = new WorkspaceSet();
        set.Reconcile(Desktop([window], Monitor(FirstMonitor), Monitor(SecondMonitor, "Right")));
        var id = set.WorkspaceFor(window.Id)!.Value;
        var tree = set.Trees[id];
        var root = tree.Snapshot;
        set.Reconcile(Desktop([window], Monitor(FirstMonitor)));
        Assert.Equal(id, set.WorkspaceFor(window.Id));
        Assert.All(set.Snapshot, w => Assert.Equal(FirstMonitor, w.MonitorId));
        Assert.Single(set.Snapshot, w => w.Active);
        Assert.Same(tree, set.Trees[id]);
        Assert.Equal(root, tree.Snapshot);
    }

    [Fact]
    public void ZeroMonitorsRetainWindowsAndRecoverDeterministically()
    {
        var window = Window();
        var set = new WorkspaceSet();
        set.Reconcile(Desktop([window]));
        var id = set.WorkspaceFor(window.Id);
        var tree = set.Trees[id!.Value];
        set.Reconcile(Desktop([window], Monitor(SecondMonitor, "Right")));
        Assert.Equal(id, set.WorkspaceFor(window.Id));
        Assert.Equal(SecondMonitor, set.Snapshot.Single().MonitorId);
        set.Reconcile(Desktop([window]));
        Assert.Equal(id, set.WorkspaceFor(window.Id));
        Assert.Same(tree, set.Trees[id.Value]);
        Assert.False(set.MoveWindowToMonitor(window.Id, SecondMonitor));
    }

    [Fact]
    public void MissingConfiguredMonitorResolvesWhenItAppears()
    {
        var window = Window();
        var set = new WorkspaceSet([new("RightWork", "Right")]);
        set.Reconcile(Desktop([window], Monitor(FirstMonitor, "Left")));
        var id = set.WorkspaceFor(window.Id);
        set.Reconcile(Desktop([window], Monitor(FirstMonitor, "Left"), Monitor(SecondMonitor, "Right")));
        Assert.Equal(id, set.WorkspaceFor(window.Id));
        Assert.Equal(SecondMonitor, set.Snapshot.Single(w => w.Id == id).MonitorId);
        Assert.All(set.Snapshot.GroupBy(w => w.MonitorId), g => Assert.Single(g, w => w.Active));
    }

    [Fact]
    public void DpiAndGeometryChangesPreserveWorkspaceAndTreeIdentity()
    {
        var window = Window();
        var set = new WorkspaceSet();
        set.Reconcile(Desktop([window], Monitor(FirstMonitor)));
        var before = set.Snapshot.Single();
        var tree = set.Trees[before.Id];
        var root = tree.Snapshot;
        set.Reconcile(Desktop([window with { Bounds = new(50, 50, 100, 100) }], Monitor(FirstMonitor, dpi: 144)));
        Assert.Equal(before.Id, set.Snapshot.Single().Id);
        Assert.Equal(root, tree.Snapshot);
        Assert.Equal(1920, tree.Layout(Monitor(FirstMonitor, dpi: 144), 0, 0).Placements.Single().Bounds.Width);
    }

    [Fact]
    public void VisibilityNeverShowsAppHiddenMinimizedOrFullscreenWindows()
    {
        var appHidden = Window() with { Visible = false };
        var minimized = Window() with { Visible = false, Minimized = true, ManagerHidden = true };
        var fullscreen = Window() with { Visible = false, Fullscreen = true, ManagerHidden = true };
        var managed = Window() with { Visible = false, ManagerHidden = true };
        var set = new WorkspaceSet([new("One"), new("Two")]);
        var desktop = Desktop([appHidden, minimized, fullscreen, managed], Monitor(FirstMonitor));
        set.Reconcile(desktop);
        Assert.Equal(managed.Id, set.Trees[set.Snapshot[0].Id].Snapshot!.WindowId);
        Assert.Equal(new WindowVisibility(managed.Id, true), Assert.Single(set.Visibility(desktop)));
        set.Activate(set.Snapshot[1].Id);
        Assert.Empty(set.Visibility(desktop));
        Assert.Empty(set.Visibility(desktop with { Windows = [minimized with { Visible = true }, fullscreen with { Visible = true }] }));
    }

    [Fact]
    public void FloatingWindowsParticipateInWorkspaceVisibilityButNotTrees()
    {
        var window = Window() with { Mode = WindowMode.Floating };
        var set = new WorkspaceSet([new("One"), new("Two")]);
        var desktop = Desktop([window], Monitor(FirstMonitor));
        set.Reconcile(desktop);
        Assert.All(set.Trees.Values, t => Assert.Null(t.Snapshot));
        set.Activate(set.Snapshot[1].Id);
        Assert.Equal(new WindowVisibility(window.Id, false), Assert.Single(set.Visibility(desktop)));
    }

    [Fact]
    public void NewlyDiscoveredWindowsJoinTheActiveWorkspaceAfterActivation()
    {
        var set = new WorkspaceSet([new("One"), new("Two")]);
        set.Reconcile(Desktop([], Monitor(FirstMonitor)));
        var target = set.Snapshot[1].Id;
        set.Activate(target);
        var window = Window();
        set.Reconcile(Desktop([window], Monitor(FirstMonitor)));
        Assert.Equal(target, set.WorkspaceFor(window.Id));
        Assert.Empty(set.Visibility(Desktop([window], Monitor(FirstMonitor))));
    }

    [Fact]
    public void MigrationChoosesLowestSurvivingMonitorRegardlessOfEnumerationOrder()
    {
        var removed = Guid.NewGuid();
        var window = Window(monitor: removed);
        var set = new WorkspaceSet();
        set.Reconcile(Desktop([window], Monitor(removed, "Removed")));
        var id = set.WorkspaceFor(window.Id);
        set.Reconcile(Desktop([window], Monitor(SecondMonitor, "Right"), Monitor(FirstMonitor, "Left")));
        Assert.Equal(FirstMonitor, set.Snapshot.Single(w => w.Id == id).MonitorId);
        Assert.All(set.Snapshot.GroupBy(w => w.MonitorId), g => Assert.Single(g, w => w.Active));
    }

    [Fact]
    public void ManagerHiddenMinimizedAndFullscreenWindowsRemainAssignedButLeaveTrees()
    {
        var window = Window();
        var set = new WorkspaceSet([new("One"), new("Two")]);
        set.Reconcile(Desktop([window], Monitor(FirstMonitor)));
        var id = set.WorkspaceFor(window.Id)!.Value;
        set.Activate(set.Snapshot[1].Id);
        foreach (var excluded in new[]
                 {
                     window with { Visible = false, ManagerHidden = true, Minimized = true },
                     window with { Visible = false, ManagerHidden = true, Fullscreen = true },
                     window with { Visible = false }
                 })
        {
            set.Reconcile(Desktop([excluded], Monitor(FirstMonitor)));
            Assert.Equal(id, set.WorkspaceFor(window.Id));
            Assert.Null(set.Trees[id].Snapshot);
            Assert.Empty(set.Visibility(Desktop([excluded], Monitor(FirstMonitor))));
        }
    }

    [Fact]
    public void EmptyConfigurationStillCreatesDefaultsAndSnapshotsAreReadOnly()
    {
        var set = new WorkspaceSet([]);
        set.Reconcile(Desktop([], Monitor(FirstMonitor)));
        Assert.Single(set.Snapshot);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Guid, SplitTree>)set.Trees)
            .Add(Guid.NewGuid(), new SplitTree()));
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceInfo>)set.Snapshot).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)set.Snapshot[0].WindowIds).Add(Guid.NewGuid()));
    }

    [Fact]
    public void SnapshotDoesNotChangeAfterCommands()
    {
        var window = Window();
        var set = new WorkspaceSet([new("One"), new("Two")]);
        set.Reconcile(Desktop([window], Monitor(FirstMonitor)));
        var snapshot = set.Snapshot;
        set.MoveWindow(window.Id, snapshot[1].Id);
        set.Activate(snapshot[1].Id);
        Assert.True(snapshot[0].Active);
        Assert.Equal(window.Id, snapshot[0].WindowIds.Single());
        Assert.Empty(snapshot[1].WindowIds);
    }
}