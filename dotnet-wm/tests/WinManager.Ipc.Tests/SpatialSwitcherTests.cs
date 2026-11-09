using Avalonia;
using Avalonia.Media;
using WinManager.Contracts;
using WinManager.SpatialSwitcher;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class SpatialSwitcherTests
{
    [Fact]
    public void CurrentWorkspaceLabelsUseValidNormalFontWeightByDefault()
    {
        var label = CurrentWorkspaceWindow.Label("Window", 12, Brushes.White);

        Assert.Equal(FontWeight.Normal, label.FontWeight);
        _ = new Typeface(FontFamily.Default, FontStyle.Normal, label.FontWeight, FontStretch.Normal);
    }

    [Fact]
    public void CurrentWorkspaceLabelsPreserveExplicitFontWeight()
    {
        var label = CurrentWorkspaceWindow.Label("Window", 12, Brushes.White, FontWeight.SemiBold);

        Assert.Equal(FontWeight.SemiBold, label.FontWeight);
    }

    [Fact]
    public void SearchMatchesWorkspaceNamesWithoutChangingDeskCoordinates()
    {
        var workspaces = new[]
        {
            Workspace("Code", new(-400, -160)),
            Workspace("Research", new(200, 80)),
            Workspace("Chat", new(640, 320))
        };

        var before = SpatialSwitcherPresentation.MapPositions(workspaces, 700, 330);
        var matches = workspaces.Where(workspace => SpatialSwitcherPresentation.Matches(workspace, "res")).ToArray();
        var after = SpatialSwitcherPresentation.MapPositions(workspaces, 700, 330);

        Assert.True(SpatialSwitcherPresentation.HasSpatialState(workspaces));
        Assert.Equal("Research", Assert.Single(matches).Name);
        Assert.Equal(before, after);
        Assert.False(SpatialSwitcherPresentation.Matches(workspaces[0], "res"));
    }

    [Fact]
    public void SpatialStateIsUnavailableWhenAnyWorkspaceLacksAValidPosition()
    {
        Assert.False(SpatialSwitcherPresentation.HasSpatialState([]));
        Assert.False(SpatialSwitcherPresentation.HasSpatialState([
            Workspace("Known", new(0, 0)),
            Workspace("Unknown", null)
        ]));
        Assert.Empty(SpatialSwitcherPresentation.MapPositions([Workspace("Unknown", null)], 700, 330));
    }

    [Theory]
    [InlineData(Direction.Left, -1, 0)]
    [InlineData(Direction.Right, 1, 0)]
    [InlineData(Direction.Up, 0, -1)]
    [InlineData(Direction.Down, 0, 1)]
    public void DirectionalNavigationChoosesNearestWorkspaceInTheRequestedDirection(
        Direction direction, int expectedX, int expectedY)
    {
        var center = Workspace("Center", new(0, 0));
        var target = Workspace("Target", new(expectedX * 100, expectedY * 100));
        var other = Workspace("Other", new(-expectedX * 100, -expectedY * 100));

        Assert.Equal(target.Id, SpatialSwitcherPresentation.DirectionalTarget(center.Id,
            [center, target, other], direction));
    }

    [Fact]
    public void ActivationCommandAlwaysIncludesTheExplicitMonitorTarget()
    {
        var workspaceId = Guid.NewGuid();
        var monitorId = Guid.NewGuid();

        var command = SpatialSwitcherPresentation.ActivationCommand(workspaceId, monitorId);

        Assert.Equal(CommandKind.ActivateWorkspace, command.Kind);
        Assert.Equal(workspaceId, command.WorkspaceId);
        Assert.Equal(monitorId, command.MonitorId);
        Assert.Throws<ArgumentException>(() => SpatialSwitcherPresentation.ActivationCommand(workspaceId, Guid.Empty));
    }

    [Fact]
    public void KeyboardPolicyCapturesAltTabWinTabAndWinSpaceChords()
    {
        var policy = new SwitcherKeyboardPolicy();

        var altTab = policy.Process(0, 0x09, true, false, false, false, true, false, true);
        Assert.True(altTab.Suppress);
        Assert.Equal(SwitcherKeyboardAction.CycleWindowForward, altTab.Action);
        Assert.True(policy.Process(0, 0x09, false, false, false, false, false, false, false).Suppress);

        var winSpace = policy.Process(0, 0x20, true, false, true, true, false, false, true);
        Assert.True(winSpace.Suppress);
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceBackward, winSpace.Action);
        Assert.True(policy.Process(0, 0x20, false, false, false, false, false, false, false).Suppress);

        var winTab = policy.Process(0, 0x09, true, false, true, false, false, false, true);
        Assert.True(winTab.Suppress);
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceForward, winTab.Action);
        Assert.True(policy.Process(0, 0x09, false, false, false, false, false, false, false).Suppress);

        var reverseWinTab = policy.Process(0, 0x09, true, false, true, true, false, false, true);
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceBackward, reverseWinTab.Action);

        var controlTabPolicy = new SwitcherKeyboardPolicy();
        Assert.False(controlTabPolicy.Process(0, 0x09, true, true, false, false, false, false, true).Suppress);
    }

    [Fact]
    public void KeyboardPolicyPassesUnrelatedAndInactiveKeysAndPairsConsumedReleases()
    {
        var policy = new SwitcherKeyboardPolicy();

        Assert.False(policy.Process(-1, 0x09, true, true, false, false, false, false, true).Suppress);
        Assert.False(policy.Process(0, 0x09, true, true, false, false, true, false, true).Suppress);
        Assert.False(policy.Process(0, 0x09, true, false, true, false, false, false, false).Suppress);
        Assert.False(policy.Process(0, 0x09, true, false, false, false, true, false, false).Suppress);
        Assert.False(policy.Process(0, 0x09, true, false, false, false, true, true, true).Suppress);
        Assert.False(policy.Process(0, 0x41, true, false, false, false, true, false, true).Suppress);
        Assert.False(policy.Process(0, 0x09, true, false, true, false, false, true, true).Suppress);

        Assert.True(policy.Process(0, 0x09, true, false, false, false, true, false, true).Suppress);
        Assert.True(policy.Process(0, 0x09, false, false, false, false, false, false, false).Suppress);
        Assert.False(policy.Process(0, 0x09, false, false, false, false, false, false, true).Suppress);
    }

    [Fact]
    public void KeyboardPolicyRepeatsOnlyTheActiveChordAndSuppressesItsMatchingRelease()
    {
        var policy = new SwitcherKeyboardPolicy();

        Assert.Equal(SwitcherKeyboardAction.CycleWindowForward,
            policy.Process(0, 0x09, true, false, false, false, true, false, true).Action);
        Assert.Equal(SwitcherKeyboardAction.CycleWindowForward,
            policy.Process(0, 0x09, true, false, false, false, true, false, true).Action);
        Assert.True(policy.Process(0, 0x09, false, false, false, false, false, false, true).Suppress);
        Assert.False(policy.Process(0, 0x09, true, false, false, false, false, false, true).Suppress);

        var workspacePolicy = new SwitcherKeyboardPolicy();
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceForward,
            workspacePolicy.Process(0, 0x09, true, false, true, false, false, false, true).Action);
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceForward,
            workspacePolicy.Process(0, 0x09, true, false, true, false, false, false, true).Action);
        Assert.True(workspacePolicy.Process(0, 0x09, false, false, false, false, false, false, true).Suppress);
    }

    [Fact]
    public void KeyboardPolicyPairsWindowAndWorkspaceShortcutReleasesIndependently()
    {
        var policy = new SwitcherKeyboardPolicy();

        Assert.Equal(SwitcherKeyboardAction.CycleWindowForward,
            policy.Process(0, 0x09, true, false, false, false, true, false, true).Action);
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceForward,
            policy.Process(0, 0x20, true, false, true, false, false, false, true).Action);
        Assert.Equal(SwitcherKeyboardAction.CycleWorkspaceForward,
            policy.Process(0, 0x09, true, false, true, false, false, false, true).Action);
        Assert.True(policy.Process(0, 0x09, false, false, false, false, false, false, true).Suppress);
        Assert.True(policy.Process(0, 0x20, false, false, false, false, false, false, true).Suppress);
        Assert.False(policy.Process(0, 0x09, false, false, false, false, false, false, true).Suppress);
    }

    [Fact]
    public void WindowAndWorkspaceCyclingWrapAndRespectWorkspaceMembership()
    {
        var windows = new[]
        {
            Window("Editor"),
            Window("Floating", mode: WindowMode.Floating),
            Window("Overflow", visible: false, minimized: true, overflow: true),
            Window("Other workspace"),
            Window("Ignored", ignored: true)
        };
        var code = Workspace("Code", new(0, 0)) with
            { WindowIds = [windows[0].Id, windows[1].Id, windows[2].Id, windows[4].Id] };
        var chat = Workspace("Chat", new(200, 0)) with { WindowIds = [windows[3].Id] };
        var candidates = SpatialSwitcherPresentation.WindowsForWorkspace(code, windows);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(windows[1].Id, SpatialSwitcherPresentation.CycleWindow(windows[0].Id, candidates, false));
        Assert.Equal(windows[0].Id, SpatialSwitcherPresentation.CycleWindow(windows[2].Id, candidates, false));
        Assert.Equal(windows[2].Id, SpatialSwitcherPresentation.CycleWindow(windows[0].Id, candidates, true));
        Assert.Equal(chat.Id, SpatialSwitcherPresentation.CycleWorkspace(code.Id, [code, chat], false));
        Assert.Equal(chat.Id, SpatialSwitcherPresentation.CycleWorkspace(code.Id, [code, chat], true));
        Assert.Null(SpatialSwitcherPresentation.CycleWindow(null, [], false));
    }

    [Fact]
    public void CurrentWorkspaceUsesFocusedMembershipThenActiveFallbackAndFocusCommandIsExplicit()
    {
        var code = Workspace("Code", new(0, 0));
        var chat = Workspace("Chat", new(200, 0)) with { Active = true };
        var window = Window("Editor");
        code = code with { WindowIds = [window.Id] };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([], [window], window.Id), new Dictionary<Guid, SplitNodeInfo?>(), false,
            [code, chat]);

        Assert.Equal(code.Id, SpatialSwitcherPresentation.CurrentWorkspaceId(snapshot));
        Assert.Equal(new ManagerCommand(CommandKind.Focus, WindowId: window.Id),
            SpatialSwitcherPresentation.FocusCommand(window.Id));
        Assert.Throws<ArgumentException>(() => SpatialSwitcherPresentation.FocusCommand(Guid.Empty));

        snapshot = snapshot with { Desktop = snapshot.Desktop with { FocusedWindowId = null } };
        Assert.Equal(chat.Id, SpatialSwitcherPresentation.CurrentWorkspaceId(snapshot));
    }

    [Fact]
    public void CurrentWorkspaceContextFollowsForegroundMonitorEvenWhenItsViewIsEmpty()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var editor = Window("Editor") with { MonitorId = left };
        var code = Workspace("Code", new(0, 0)) with
            { MonitorId = left, Active = true, WindowIds = [editor.Id] };
        var empty = Workspace("Empty", new(200, 0)) with
            { MonitorId = right, Active = true };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([Monitor(left), Monitor(right)], [editor], editor.Id),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [code, empty], null,
            [new(left, code.Id), new(right, empty.Id)]);

        Assert.Equal(empty.Id, SpatialSwitcherPresentation.CurrentWorkspaceId(snapshot, right));
        Assert.Equal(new SwitcherContext(empty.Id, null, right),
            SpatialSwitcherPresentation.CaptureContext(snapshot, right));
    }

    [Fact]
    public void WindowGroupsSeparateVisibleWindowsFromMinimizedAndOverflowWindows()
    {
        var active = Window("Active");
        var minimized = Window("Minimized", visible: false, minimized: true);
        var overflow = Window("Overflow", visible: false, minimized: true, overflow: true);
        var ignored = Window("Ignored", ignored: true);
        var workspace = Workspace("Code", new(0, 0)) with
            { WindowIds = [active.Id, minimized.Id, overflow.Id, ignored.Id] };

        var groups = SpatialSwitcherPresentation.WindowGroupsForWorkspace(workspace,
            [active, minimized, overflow, ignored]);

        Assert.Equal([active.Id], groups.Active.Select(window => window.Id));
        Assert.Equal([minimized.Id, overflow.Id], groups.Inactive.Select(window => window.Id));
    }

    [Fact]
    public void PopupCenteringUsesMonitorDpiAndKeepsNegativeCoordinates()
    {
        var position = PopupPlacement.Center(new(-1920, -120, 1920, 1080), 400, 200, 144);

        Assert.Equal(new PixelPoint(-1260, 270), position);
    }

    [Fact]
    public void PopupCenteringClampsOversizedWindowToWorkArea()
    {
        var position = PopupPlacement.Center(new(100, 50, 500, 300), 1000, 1000, 96);

        Assert.Equal(new PixelPoint(100, 50), position);
    }

    [Fact]
    public void PopupMonitorFallbackPrefersRequestedMonitorThenStablePrimary()
    {
        var primary = Monitor(Guid.NewGuid());
        var secondary = Monitor(Guid.NewGuid()) with
            { DeviceName = "DISPLAY-Z", Bounds = new(1920, 0, 1920, 1080) };

        Assert.Equal(secondary.Id, PopupPlacement.FromSnapshot([primary, secondary], secondary.Id)?.MonitorId);
        Assert.Equal(primary.Id, PopupPlacement.FromSnapshot([secondary, primary])?.MonitorId);
        Assert.Null(PopupPlacement.FromSnapshot([]));
    }

    [Fact]
    public void CaptureContextUsesCurrentFocusAndMonitorForEachSummon()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var code = Workspace("Code", new(0, 0));
        var chat = Workspace("Chat", new(200, 0));
        var editor = Window("Editor") with { MonitorId = left };
        var browser = Window("Browser") with { MonitorId = right };
        code = code with { WindowIds = [editor.Id], Active = true, MonitorId = left };
        chat = chat with { WindowIds = [browser.Id], Active = true, MonitorId = right };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([Monitor(left), Monitor(right)], [editor, browser], editor.Id),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [code, chat]);

        Assert.Equal(new SwitcherContext(code.Id, editor.Id, left),
            SpatialSwitcherPresentation.CaptureContext(snapshot));

        snapshot = snapshot with { Desktop = snapshot.Desktop with { FocusedWindowId = browser.Id } };
        Assert.Equal(new SwitcherContext(chat.Id, browser.Id, right),
            SpatialSwitcherPresentation.CaptureContext(snapshot));
    }

    private static WorkspaceInfo Workspace(string name, DeskPosition? position) =>
        new(Guid.NewGuid(), name, Guid.Empty, false, [], WorkspaceLayout.Split, position);

    private static WindowInfo Window(string title, bool visible = true, bool minimized = false,
        bool overflow = false, bool ignored = false, WindowMode mode = WindowMode.Tiling) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, "process", "window", new(0, 0, 300, 200), visible,
            minimized, ignored, mode, LayoutOverflow: overflow);

    private static MonitorInfo Monitor(Guid id) =>
        new(id, id.ToString(), new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96);
}