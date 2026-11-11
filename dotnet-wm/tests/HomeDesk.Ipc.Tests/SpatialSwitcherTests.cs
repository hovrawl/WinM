using System.Threading.Channels;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using WinManager.Contracts;
using WinManager.SpatialSwitcher;
using WinManager.Windows;
using Xunit;
using ContractPixelRect = WinManager.Contracts.PixelRect;

namespace WinManager.Ipc.Tests;

public sealed class SpatialSwitcherTests
{
    [Fact]
    public void GeneralSettingsPreserveOtherConfigurationFieldsAndEnforceGapLimits()
    {
        const string original =
            """{"workspaces":[{"name":"Code","layout":"layered"}],"innerGap":8,"outerGap":8,"ignoredProcessNames":["sample.exe"],"maximumClients":16,"enableDefaultShortcuts":true,"monitorOrder":["DISPLAY2"],"touchpad":{"enabled":true,"completeThreeFingerSwipesOnFirstLift":false}}""";

        var settings = GeneralSettingsPresentation.Read(original);
        Assert.Equal(8, settings.InnerGap);
        Assert.Equal(8, settings.OuterGap);
        Assert.True(settings.EnableDefaultShortcuts);
        Assert.True(settings.TouchpadEnabled);
        Assert.Equal(["DISPLAY2"], settings.MonitorOrder);
        var updated = GeneralSettingsPresentation.Update(original, 24, 12, false, false, ["DISPLAY3", "DISPLAY1"]);
        using var document = JsonDocument.Parse(updated);
        var root = document.RootElement;
        Assert.Equal(24, root.GetProperty("innerGap").GetInt32());
        Assert.Equal(12, root.GetProperty("outerGap").GetInt32());
        Assert.False(root.GetProperty("enableDefaultShortcuts").GetBoolean());
        Assert.False(root.GetProperty("touchpad").GetProperty("enabled").GetBoolean());
        Assert.False(root.GetProperty("touchpad").GetProperty("completeThreeFingerSwipesOnFirstLift").GetBoolean());
        Assert.Equal(new[] { "DISPLAY3", "DISPLAY1" }, root.GetProperty("monitorOrder").EnumerateArray()
            .Select(monitor => monitor.GetString()).ToArray());
        Assert.Equal("Code", root.GetProperty("workspaces")[0].GetProperty("name").GetString());
        Assert.Equal("sample.exe", root.GetProperty("ignoredProcessNames")[0].GetString());
        Assert.Equal(16, root.GetProperty("maximumClients").GetInt32());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeneralSettingsPresentation.Update(original, 257, 12, false, false, ["DISPLAY2"]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeneralSettingsPresentation.Update(original, 24, -1, false, false, ["DISPLAY2"]));
    }

    [Fact]
    public void WorkspaceLayoutCommandTargetsTheSelectedWorkspaceAndCarriesLayeredOptions()
    {
        var workspaceId = Guid.NewGuid();
        var options = new LayeredLayoutOptions(0.73, 24);

        var command = WorkspaceDetailsControl.CreateLayoutCommand(workspaceId, WorkspaceLayout.Layered, options);

        Assert.Equal(CommandKind.SetWorkspaceLayout, command.Kind);
        Assert.Equal(workspaceId, command.WorkspaceId);
        Assert.Equal(WorkspaceLayout.Layered, command.Layout);
        Assert.Equal(options, command.LayeredLayoutOptions);
    }

    [Fact]
    public void WorkspaceLayoutTogglePreservesExistingOptionsAndUsesDefaultsForOlderSnapshots()
    {
        var options = new LayeredLayoutOptions(0.65, 12);
        var workspace = Workspace("Layered", new(0, 0)) with { LayeredLayoutOptions = options };

        var toggle = WorkspaceDetailsControl.CreateToggleLayoutCommand(workspace, WorkspaceLayout.Split);
        var olderSnapshotToggle = WorkspaceDetailsControl.CreateToggleLayoutCommand(
            workspace with { LayeredLayoutOptions = null }, WorkspaceLayout.Layered);

        Assert.Equal(workspace.Id, toggle.WorkspaceId);
        Assert.Equal(WorkspaceLayout.Split, toggle.Layout);
        Assert.Equal(options, toggle.LayeredLayoutOptions);
        Assert.Equal(LayeredLayoutOptions.Default, olderSnapshotToggle.LayeredLayoutOptions);
    }

    [Fact]
    public async Task WorkspaceDetailsToggleButtonSubmitsForTheCurrentWorkspace()
    {
        var commandReceived =
            new TaskCompletionSource<ManagerCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var details = new WorkspaceDetailsControl(command =>
        {
            commandReceived.SetResult(command);
            return Task.FromResult(new CommandResult(true, null, null, 2));
        });
        var options = new LayeredLayoutOptions(0.7, 18);
        var workspace = Workspace("Code", new(0, 0)) with { LayeredLayoutOptions = options };
        details.UpdateWorkspace(workspace, true);

        var card = Assert.IsType<Border>(details.Content);
        var rows = Assert.IsType<StackPanel>(card.Child);
        var layoutButtons = Assert.IsType<StackPanel>(rows.Children[1]);
        var settings = Assert.IsType<Grid>(rows.Children[2]);
        Assert.IsType<TextBox>(Assert.IsType<StackPanel>(settings.Children[0]).Children[1]);
        Assert.IsType<TextBox>(Assert.IsType<StackPanel>(settings.Children[1]).Children[1]);
        var layeredButton = Assert.IsType<Button>(layoutButtons.Children[1]);
        Assert.True(layeredButton.IsEnabled);
        layeredButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var command = await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(workspace.Id, command.WorkspaceId);
        Assert.Equal(WorkspaceLayout.Layered, command.Layout);
        Assert.Equal(options, command.LayeredLayoutOptions);
    }

    [Fact]
    public void WorkspaceDetailsParsesAndValidatesLayeredOptions()
    {
        Assert.True(WorkspaceDetailsControl.TryParseLayeredOptions("72.5", "16", out var options, out _));
        Assert.Equal(new LayeredLayoutOptions(0.725, 16), options);

        Assert.False(WorkspaceDetailsControl.TryParseLayeredOptions("0", "16", out _, out var widthError));
        Assert.Contains("Width", widthError);
        Assert.False(WorkspaceDetailsControl.TryParseLayeredOptions("101", "16", out _, out _));
        Assert.False(WorkspaceDetailsControl.TryParseLayeredOptions("NaN", "16", out _, out _));
        Assert.False(WorkspaceDetailsControl.TryParseLayeredOptions("80", "16.5", out _, out var spacingError));
        Assert.Contains("spacing", spacingError);
        Assert.False(WorkspaceDetailsControl.TryParseLayeredOptions("80", "257", out _, out _));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    public void PopupKeysRemainAvailableToFocusedWorkspaceEditors(Key key)
    {
        Assert.False(WorkspaceDetailsControl.ShouldHandlePopupKey(true, key));
        Assert.True(WorkspaceDetailsControl.ShouldHandlePopupKey(false, key));
    }

    [Fact]
    public void SwitcherThemeResolvesSystemPaletteAndUpdatesSharedBrushes()
    {
        var light = SwitcherTheme.ResolvePalette(SwitcherThemeMode.System, ThemeVariant.Light);
        var dark = SwitcherTheme.ResolvePalette(SwitcherThemeMode.System, ThemeVariant.Dark);
        var forcedLight = SwitcherTheme.ResolvePalette(SwitcherThemeMode.Light, ThemeVariant.Dark);
        var forcedDark = SwitcherTheme.ResolvePalette(SwitcherThemeMode.Dark, ThemeVariant.Light);
        var sharedCanvas = SwitcherTheme.Canvas;
        var sharedShape = SwitcherTheme.WindowShapePalette[0];

        Assert.Equal(Color.Parse("#F4F1EB"), light.Canvas);
        Assert.Equal(Color.Parse("#171A1D"), dark.Canvas);
        Assert.Equal(light.Canvas, forcedLight.Canvas);
        Assert.Equal(dark.Canvas, forcedDark.Canvas);

        try
        {
            SwitcherTheme.ApplyPalette(dark);
            Assert.Same(sharedCanvas, SwitcherTheme.Canvas);
            Assert.Equal(dark.Canvas, sharedCanvas.Color);
            Assert.Equal(dark.WindowShapes[0], sharedShape.Color);

            SwitcherTheme.ApplyPalette(light);
            Assert.Equal(light.Canvas, sharedCanvas.Color);
            Assert.Equal(light.WindowShapes[0], sharedShape.Color);
        }
        finally
        {
            SwitcherTheme.ApplyPalette(SwitcherTheme.ResolvePalette(SwitcherThemeMode.System, ThemeVariant.Light));
        }
    }

    [Fact]
    public async Task ConnectionRetryRepeatsFailuresRecoversSkipsConnectedAndCancels()
    {
        var delays = Channel.CreateUnbounded<TaskCompletionSource<bool>>();
        using var cancellation = new CancellationTokenSource();
        var connected = false;
        var attempts = 0;
        var retry = ConnectionRetry.RunAsync(_ =>
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("Connection failed.");
            if (attempts == 3) connected = true;
            return Task.CompletedTask;
        }, () => connected, TimeSpan.FromSeconds(5), (interval, token) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), interval);
            var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            delays.Writer.TryWrite(wait);
            return wait.Task.WaitAsync(token);
        }, cancellation.Token);

        var firstDelay = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, attempts);
        firstDelay.SetResult(true);

        var secondDelay = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
        Assert.False(connected);
        secondDelay.SetResult(true);

        var connectedDelay = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, attempts);
        Assert.True(connected);
        connected = false;
        connectedDelay.SetResult(true);

        var recoveryDelay = await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, attempts);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        recoveryDelay.SetResult(true);
    }

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
    public void AcceptKeyPolicyAcceptsEnterAndUnmodifiedSpace()
    {
        Assert.True(SwitcherAcceptKeyPolicy.IsAcceptKey(Key.Enter, KeyModifiers.None));
        Assert.True(SwitcherAcceptKeyPolicy.IsAcceptKey(Key.Space, KeyModifiers.None));
    }

    [Theory]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Meta)]
    public void AcceptKeyPolicyDoesNotAcceptModifiedOrSearchFieldSpace(KeyModifiers modifiers)
    {
        Assert.False(SwitcherAcceptKeyPolicy.IsAcceptKey(Key.Space, modifiers));
        Assert.False(SwitcherAcceptKeyPolicy.IsAcceptKey(Key.Space, KeyModifiers.None, textInputFocused: true));
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
    public void CarouselBrowseChangesSelectionWithoutFocusingAndAcceptFocusesOnlySelectedWindow()
    {
        var workspaceId = Guid.NewGuid();
        var original = Window("Original");
        var other = Window("Other");
        var state = new CarouselState();
        state.BeginOpen(new SwitcherContext(workspaceId, original.Id, original.MonitorId), [original, other]);

        Assert.Equal(CarouselLifecycleState.Opening, state.Lifecycle);
        Assert.Null(state.Accept());
        Assert.True(state.MarkOpen());
        Assert.True(state.Select(other.Id));
        Assert.Equal(original.Id, state.OriginalWindowId);
        Assert.Equal(other.Id, state.SelectedWindowId);

        var accepted = state.Accept();

        Assert.Equal(CarouselLifecycleState.Closing, state.Lifecycle);
        Assert.Equal(new ManagerCommand(CommandKind.Focus, WindowId: other.Id), accepted);
    }

    [Fact]
    public void CarouselCancelAndExternalActivationNeverCreateAnAcceptanceCommand()
    {
        var original = Window("Original");
        var other = Window("Other");
        var state = new CarouselState();
        state.BeginOpen(new SwitcherContext(Guid.NewGuid(), original.Id, original.MonitorId), [original, other]);
        state.MarkOpen();
        state.Select(other.Id);

        Assert.Equal(original.Id, state.Cancel());
        Assert.Equal(CarouselLifecycleState.Closing, state.Lifecycle);
        state.CompleteClose();
        Assert.Equal(CarouselLifecycleState.Closed, state.Lifecycle);
        Assert.Null(state.SelectedWindowId);

        state.BeginOpen(new SwitcherContext(Guid.NewGuid(), original.Id, original.MonitorId), [original, other]);
        state.MarkOpen();
        state.CloseExternally();
        Assert.Equal(CarouselLifecycleState.Closed, state.Lifecycle);
        Assert.Null(state.Accept());
    }

    [Fact]
    public void CarouselScrollClampsAtStripEndsAndMovesSelectionUnderViewportAnchor()
    {
        var windows = new[] { Window("A"), Window("B"), Window("C"), Window("D") };
        var state = new CarouselState();
        state.BeginOpen(new SwitcherContext(Guid.NewGuid(), windows[0].Id, windows[0].MonitorId), windows);
        state.MarkOpen();
        var maximumOffset = CarouselPresentation.MaximumScrollOffset(windows.Length, 800, 400);
        var stride = CarouselPresentation.CardStride(800);
        var cardWidth = CarouselPresentation.CardWidth(800);

        Assert.True(state.ScrollBy(500, maximumOffset, stride, cardWidth, 400));
        Assert.Equal(500, state.HorizontalOffset);
        Assert.Equal(windows[1].Id, state.SelectedWindowId);
        Assert.True(state.ScrollBy(10000, maximumOffset, stride, cardWidth, 400));
        Assert.Equal(maximumOffset, state.HorizontalOffset);
        Assert.Equal(windows[3].Id, state.SelectedWindowId);
        Assert.True(state.ScrollBy(-10000, maximumOffset, stride, cardWidth, 400));
        Assert.Equal(0, state.HorizontalOffset);
        Assert.Equal(windows[0].Id, state.SelectedWindowId);
    }

    [Fact]
    public void CarouselSnapshotReconciliationPreservesSelectionByIdAndFallsBackWhenRemoved()
    {
        var original = Window("Original");
        var selected = Window("Selected");
        var remaining = Window("Remaining");
        var state = new CarouselState();
        state.BeginOpen(new SwitcherContext(Guid.NewGuid(), original.Id, original.MonitorId),
            [original, selected, remaining]);
        state.MarkOpen();
        state.Select(selected.Id);

        state.UpdateCandidates([original, selected with { Title = "Selected renamed" }, remaining]);
        Assert.Equal(selected.Id, state.SelectedWindowId);

        state.UpdateCandidates([original, remaining]);
        Assert.Equal(original.Id, state.SelectedWindowId);
        Assert.Equal(original.Id, state.Cancel());
    }

    [Fact]
    public void CarouselCapturedViewClosesWhenWorkspaceOrMonitorIsRemovedOrSwitched()
    {
        var monitor = Monitor(Guid.NewGuid());
        var workspace = Workspace("Captured", new(0, 0)) with { Active = true, MonitorId = monitor.Id };
        var otherWorkspace = Workspace("Other", new(400, 0)) with { Active = true, MonitorId = monitor.Id };
        var context = new SwitcherContext(workspace.Id, null, monitor.Id);
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1, new DesktopSnapshot([monitor], [], null),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [workspace, otherWorkspace], null,
            [new(monitor.Id, workspace.Id)]);

        Assert.True(CarouselWindow.IsCapturedViewValid(snapshot, context));
        Assert.False(CarouselWindow.IsCapturedViewValid(snapshot with
        {
            WorkspaceViews = [new(monitor.Id, otherWorkspace.Id)]
        }, context));
        Assert.False(CarouselWindow.IsCapturedViewValid(snapshot with
        {
            Desktop = snapshot.Desktop with { Monitors = [] }
        }, context));
        Assert.False(CarouselWindow.IsCapturedViewValid(snapshot with
        {
            Workspaces = [otherWorkspace]
        }, context));
    }

    [Theory]
    [InlineData(TouchpadSwipeDirection.Up, "Open")]
    [InlineData(TouchpadSwipeDirection.Down, "Cancel")]
    public void SwitcherRoutesVerticalThreeFingerSwipesWhenContactsAreObserved(
        TouchpadSwipeDirection direction, string expected)
    {
        var capability = new TouchpadCapabilityResult(TouchpadCapabilityState.GestureConflictBlocked, 1, true,
            false, "shell suppression unverified");
        var gesture = new TouchpadGesture(TouchpadGestureKind.Swipe, 3, direction, 0, 100);

        Assert.Equal(Enum.Parse<CarouselGestureAction>(expected), SwitcherTouchpadPolicy.Route(gesture, capability));
    }

    [Fact]
    public void SwitcherDoesNotRouteHostHorizontalGestures()
    {
        var available = new TouchpadCapabilityResult(TouchpadCapabilityState.Available, 1, true, true, null);

        Assert.Null(SwitcherTouchpadPolicy.Route(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 3, TouchpadSwipeDirection.Left, 0, 100), available));
    }

    [Fact]
    public void SwitcherRoutesLocalInputWithoutClaimingExclusiveShellOwnership()
    {
        var capability = new TouchpadCapabilityResult(TouchpadCapabilityState.GestureConflictBlocked, 1, true,
            false, "shell suppression unverified");

        Assert.Equal(CarouselGestureAction.Open, SwitcherTouchpadPolicy.Route(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 3, TouchpadSwipeDirection.Up, 0, 100), capability));
        Assert.True(SwitcherTouchpadPolicy.TryGetScrollDelta(
            new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 2, null, -0.07, 100), capability,
            out var delta));
        Assert.Equal(0.07, delta, 6);
        Assert.False(SwitcherTouchpadPolicy.TryGetScrollDelta(
            new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 3, null, 0.1, 100), capability, out _));
    }

    [Theory]
    [InlineData(0.07, -0.07)]
    [InlineData(-0.07, 0.07)]
    public void SwitcherInvertsBothHorizontalScrollDirections(double inputDelta, double expectedDelta)
    {
        var capability = new TouchpadCapabilityResult(TouchpadCapabilityState.GestureConflictBlocked, 1, true,
            false, "shell suppression unverified");

        Assert.True(SwitcherTouchpadPolicy.TryGetScrollDelta(
            new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 2, null, inputDelta, 100), capability,
            out var delta));
        Assert.Equal(expectedDelta, delta, 6);
    }

    [Theory]
    [InlineData(TouchpadCapabilityState.Disabled, 1, true)]
    [InlineData(TouchpadCapabilityState.UnsupportedDevice, 0, true)]
    [InlineData(TouchpadCapabilityState.RegistrationFailed, 1, true)]
    [InlineData(TouchpadCapabilityState.DecodingFailed, 1, true)]
    [InlineData(TouchpadCapabilityState.GestureConflictBlocked, 0, true)]
    [InlineData(TouchpadCapabilityState.GestureConflictBlocked, 1, false)]
    public void SwitcherRejectsLocalInputWithoutUsableContactReports(TouchpadCapabilityState state, int deviceCount,
        bool contactsObserved)
    {
        var capability = new TouchpadCapabilityResult(state, deviceCount, contactsObserved, false, "diagnostic");

        Assert.Null(SwitcherTouchpadPolicy.Route(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 3, TouchpadSwipeDirection.Up, 0, 100), capability));
        Assert.False(SwitcherTouchpadPolicy.TryGetScrollDelta(
            new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 2, null, 0.1, 100), capability, out _));
    }

    [Fact]
    public void SwitcherReadsSharedTouchpadSettingsAndDefaultsOnForOlderConfiguration()
    {
        var legacy = new ConfigurationDocument("config.json", "{}", "revision", true, null);
        var disabled = new ConfigurationDocument("config.json", "{\"touchpad\":{\"enabled\":false}}",
            "revision", true, null);
        var custom = new ConfigurationDocument("config.json",
            "{\"touchpad\":{\"enabled\":true,\"completeThreeFingerSwipesOnFirstLift\":false}}",
            "revision", true, null);
        var invalid = new ConfigurationDocument("config.json", "not json", "revision", false, "invalid");

        Assert.True(SwitcherWindow.ReadTouchpadEnabled(legacy));
        Assert.False(SwitcherWindow.ReadTouchpadEnabled(disabled));
        Assert.True(SwitcherWindow.ReadTouchpadEnabled(invalid));
        Assert.Equal(new TouchpadOptions(), SwitcherWindow.ReadTouchpadOptions(legacy));
        Assert.True(SwitcherWindow.ReadTouchpadOptions(disabled).CompleteThreeFingerSwipesOnFirstLift);
        Assert.False(SwitcherWindow.ReadTouchpadOptions(custom).CompleteThreeFingerSwipesOnFirstLift);
        Assert.Equal(new TouchpadOptions(), SwitcherWindow.ReadTouchpadOptions(invalid));
    }

    [Fact]
    public void CtrlAltSpaceTogglesCarouselOnceAndConsumesItsMatchingRelease()
    {
        var policy = new SwitcherKeyboardPolicy();

        var open = policy.Process(0, 0x20, true, true, false, false, true, false, true);
        var repeat = policy.Process(0, 0x20, true, true, false, false, true, false, true);
        var release = policy.Process(0, 0x20, false, false, false, false, false, false, true);

        Assert.True(open.Suppress);
        Assert.Equal(SwitcherKeyboardAction.ToggleCarousel, open.Action);
        Assert.True(repeat.Suppress);
        Assert.Null(repeat.Action);
        Assert.True(release.Suppress);
        Assert.False(policy.Process(0, 0x20, false, false, false, false, false, false, true).Suppress);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    public void CarouselKeybindRejectsInjectedDisabledOrModifiedChords(bool enabled, bool injected,
        bool altDown, bool shiftDown)
    {
        var policy = new SwitcherKeyboardPolicy();

        var decision = policy.Process(0, 0x20, true, true, false, shiftDown, altDown, injected, enabled);

        Assert.False(decision.Suppress);
        Assert.Null(decision.Action);
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
        {
            WindowIds = [windows[0].Id, windows[1].Id, windows[2].Id, windows[4].Id]
        };
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
        {
            MonitorId = left, Active = true, WindowIds = [editor.Id]
        };
        var empty = Workspace("Empty", new(200, 0)) with
        {
            MonitorId = right, Active = true
        };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([Monitor(left), Monitor(right)], [editor], editor.Id),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [code, empty], null,
            [new(left, code.Id), new(right, empty.Id)]);

        Assert.Equal(empty.Id, SpatialSwitcherPresentation.CurrentWorkspaceId(snapshot, right));
        Assert.Equal(new SwitcherContext(empty.Id, null, right),
            SpatialSwitcherPresentation.CaptureContext(snapshot, right));
    }

    [Fact]
    public void CurrentWorkspaceContextStaysOnCapturedMonitorAcrossFocusUpdates()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var leftWindow = Window("Left window") with { MonitorId = left };
        var rightWindow = Window("Right window") with { MonitorId = right };
        var leftWorkspace = Workspace("Left workspace", new(0, 0)) with
        {
            MonitorId = left, Active = true, WindowIds = [leftWindow.Id]
        };
        var rightWorkspace = Workspace("Right workspace", new(200, 0)) with
        {
            MonitorId = right, Active = true, WindowIds = [rightWindow.Id]
        };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([Monitor(left), Monitor(right)], [leftWindow, rightWindow], leftWindow.Id),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [leftWorkspace, rightWorkspace], null,
            [new(left, leftWorkspace.Id), new(right, rightWorkspace.Id)]);
        var capturedContext = SpatialSwitcherPresentation.CaptureContext(snapshot, right);

        Assert.Equal(new SwitcherContext(rightWorkspace.Id, rightWindow.Id, right), capturedContext);

        snapshot = snapshot with { Desktop = snapshot.Desktop with { FocusedWindowId = null } };
        var refreshedContext = SpatialSwitcherPresentation.CaptureContext(snapshot, capturedContext.MonitorId);

        Assert.Equal(new SwitcherContext(rightWorkspace.Id, rightWindow.Id, right), refreshedContext);
        Assert.Equal([rightWindow.Id], SpatialSwitcherPresentation.WindowsForWorkspace(
            snapshot.Workspaces!.Single(workspace => workspace.Id == refreshedContext.WorkspaceId),
            snapshot.Desktop.Windows).Select(window => window.Id));
    }

    [Fact]
    public void CurrentWorkspaceContextKeepsEmptyCapturedMonitorViewAfterFocusMovesElsewhere()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var leftWindow = Window("Left window") with { MonitorId = left };
        var leftWorkspace = Workspace("Left workspace", new(0, 0)) with
        {
            MonitorId = left, Active = true, WindowIds = [leftWindow.Id]
        };
        var emptyWorkspace = Workspace("Empty workspace", new(200, 0)) with
        {
            MonitorId = right, Active = true
        };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([Monitor(left), Monitor(right)], [leftWindow], leftWindow.Id),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [leftWorkspace, emptyWorkspace], null,
            [new(left, leftWorkspace.Id), new(right, emptyWorkspace.Id)]);
        var capturedContext = SpatialSwitcherPresentation.CaptureContext(snapshot, right);

        Assert.Equal(new SwitcherContext(emptyWorkspace.Id, null, right), capturedContext);

        snapshot = snapshot with { Desktop = snapshot.Desktop with { FocusedWindowId = null } };
        var refreshedContext = SpatialSwitcherPresentation.CaptureContext(snapshot, capturedContext.MonitorId);

        Assert.Equal(new SwitcherContext(emptyWorkspace.Id, null, right), refreshedContext);
    }

    [Fact]
    public void SwitcherSnapshotUpdatesStayBoundToThePopupMonitor()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var leftWindow = Window("Left window") with { MonitorId = left };
        var rightWindow = Window("Right window") with { MonitorId = right };
        var leftWorkspace = Workspace("Left workspace", new(0, 0)) with
        {
            MonitorId = left, Active = true, WindowIds = [leftWindow.Id]
        };
        var rightWorkspace = Workspace("Right workspace", new(200, 0)) with
        {
            MonitorId = right, Active = true, WindowIds = [rightWindow.Id]
        };
        var snapshot = new ManagerSnapshot(Guid.NewGuid(), 1,
            new DesktopSnapshot([Monitor(left), Monitor(right)], [leftWindow, rightWindow], leftWindow.Id),
            new Dictionary<Guid, SplitNodeInfo?>(), false, [leftWorkspace, rightWorkspace], null,
            [new(left, leftWorkspace.Id), new(right, rightWorkspace.Id)]);
        var state = new SwitcherPopupContextState();

        state.Apply(SpatialSwitcherPresentation.CaptureContext(snapshot, right), null);
        var refreshedContext = state.Refresh(
            snapshot with { Desktop = snapshot.Desktop with { FocusedWindowId = null } }, left);

        Assert.Equal(new SwitcherContext(rightWorkspace.Id, rightWindow.Id, right), refreshedContext);
        Assert.Equal(rightWorkspace.Id, state.WorkspaceId);
        Assert.Equal(right, state.MonitorId);
    }

    [Fact]
    public void WindowGroupsSeparateVisibleWindowsFromMinimizedAndOverflowWindows()
    {
        var active = Window("Active");
        var minimized = Window("Minimized", visible: false, minimized: true);
        var overflow = Window("Overflow", visible: false, minimized: true, overflow: true);
        var ignored = Window("Ignored", ignored: true);
        var workspace = Workspace("Code", new(0, 0)) with
        {
            WindowIds = [active.Id, minimized.Id, overflow.Id, ignored.Id]
        };

        var groups = SpatialSwitcherPresentation.WindowGroupsForWorkspace(workspace,
            [active, minimized, overflow, ignored]);

        Assert.Equal([active.Id], groups.Active.Select(window => window.Id));
        Assert.Equal([minimized.Id, overflow.Id], groups.Inactive.Select(window => window.Id));
    }

    [Fact]
    public void CarouselCardsFitAspectRatioAndStayNonOverlappingWithSourceAwareClipping()
    {
        var wide = Window("Wide") with { Bounds = new ContractPixelRect(0, 0, 3200, 1800) };
        var tall = Window("Tall") with { Bounds = new ContractPixelRect(0, 0, 600, 1200) };
        var sourceSizes = new Dictionary<Guid, DwmThumbnailSourceSize>
        {
            [wide.Id] = new(1600, 900),
            [tall.Id] = new(600, 1200)
        };

        var cards = CarouselPresentation.Layout([wide, tall], sourceSizes, 1920, 1080, 1800, 680);

        Assert.Equal(2, cards.Count);
        Assert.True(cards[0].CardBounds.Right < cards[1].CardBounds.Left);
        Assert.True(cards[0].PreviewBounds.Width <= 960);
        Assert.True(cards[0].PreviewBounds.Height <= 540);
        Assert.Equal(16d / 9, cards[0].PreviewBounds.Width / cards[0].PreviewBounds.Height, 6);
        Assert.True(CarouselPresentation.TryClipPreview(cards[0].PreviewBounds, new Rect(0, 0, 500, 600),
            sourceSizes[wide.Id], out var clip));
        Assert.True(clip.DestinationBounds.Right <= 500);
        Assert.True(clip.SourceBounds.Width < sourceSizes[wide.Id].Width);
        Assert.True(clip.SourceBounds.Height < sourceSizes[wide.Id].Height);
        Assert.Equal(0, CarouselPresentation.MaximumScrollOffset(1, 1920, 1800));
        Assert.True(CarouselPresentation.MaximumScrollOffset(4, 1920, 1800) > 0);
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
        {
            DeviceName = "DISPLAY-Z", Bounds = new(1920, 0, 1920, 1080)
        };

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