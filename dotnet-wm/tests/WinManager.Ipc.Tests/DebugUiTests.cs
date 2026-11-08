using WinManager.Contracts;
using WinManager.DebugUi;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class DebugUiTests
{
    [Theory]
    [InlineData(WindowMode.Tiling, false, null, false, true)]
    [InlineData(WindowMode.Floating, false, null, true, false)]
    [InlineData(WindowMode.Tiling, true, null, true, true)]
    [InlineData(WindowMode.Floating, true, null, true, false)]
    [InlineData(WindowMode.Tiling, true, true, false, true)]
    [InlineData(WindowMode.Floating, true, false, true, false)]
    public void ModeActionsAvoidRedundancyButPermitSemanticTilingOverrides(
        WindowMode mode, bool fullscreen, bool? tilingOverride, bool canTile, bool canFloat)
    {
        var window = DemoSnapshot.Create().Desktop.Windows[0] with
        {
            Mode = mode, Fullscreen = fullscreen, TilingOverride = tilingOverride,
            IsPopup = false, IsToolWindow = false, IsOwned = false, IsNoActivate = false
        };

        Assert.Equal(canTile, InspectorPresentation.CanSetMode(window, WindowMode.Tiling, true));
        Assert.Equal(canFloat, InspectorPresentation.CanSetMode(window, WindowMode.Floating, true));
        Assert.False(InspectorPresentation.CanSetMode(window, WindowMode.Tiling, false));
        Assert.False(InspectorPresentation.CanSetMode(window, WindowMode.Floating, false));
        Assert.False(InspectorPresentation.CanSetMode(null, WindowMode.Tiling, true));
        Assert.False(InspectorPresentation.CanSetMode(null, WindowMode.Floating, true));
    }

    [Theory]
    [InlineData(WindowMode.Tiling)]
    [InlineData(WindowMode.Floating)]
    public void ModeFeedbackRetainsTheExplicitTargetForEveryOutcome(WindowMode mode)
    {
        var window = DemoSnapshot.Create().Desktop.Windows[0];
        var description = InspectorPresentation.CommandDescription(InspectorCommands.SetMode(window.Id, mode), window);
        var differentSelection = window with { Id = Guid.NewGuid(), Title = "Different selection" };

        foreach (var outcome in new[] { "Sending…", "Command completed", "Command failed", "Connection lost during command" })
        {
            var feedback = InspectorPresentation.CommandFeedback(description, outcome);
            Assert.Contains(mode.ToString(), feedback);
            Assert.Contains(window.Title, feedback);
            Assert.Contains(window.Id.ToString(), feedback);
            Assert.Contains(outcome, feedback);
            Assert.DoesNotContain(differentSelection.Id.ToString(), feedback);
        }
        Assert.Contains(window.Title, InspectorPresentation.SelectedWindow(window));
        Assert.Contains(window.Id.ToString(), InspectorPresentation.SelectedWindow(window));
        Assert.Contains(window.Mode.ToString(), InspectorPresentation.SelectedWindow(window));
        Assert.Contains("No window selected", InspectorPresentation.SelectedWindow(null));
    }

    [Fact]
    public void EverySemanticExclusionAllowsAnExplicitTilingOverride()
    {
        var window = DemoSnapshot.Create().Desktop.Windows[0] with
        {
            Mode = WindowMode.Tiling, Fullscreen = false, TilingOverride = null,
            IsPopup = false, IsToolWindow = false, IsOwned = false, IsNoActivate = false
        };
        foreach (var excluded in new[]
        {
            window with { Fullscreen = true }, window with { IsPopup = true },
            window with { IsToolWindow = true }, window with { IsOwned = true },
            window with { IsNoActivate = true }
        })
        {
            Assert.True(InspectorPresentation.CanSetMode(excluded, WindowMode.Tiling, true));
            Assert.False(InspectorPresentation.CanSetMode(excluded with { TilingOverride = true }, WindowMode.Tiling, true));
        }
    }

    [Fact]
    public void TargetFeedbackHandlesLongMissingAndMismatchedTitlesWithoutLosingIdentity()
    {
        var window = DemoSnapshot.Create().Desktop.Windows[0] with { Title = new string('x', 2000) };
        var command = InspectorCommands.SetMode(window.Id, WindowMode.Floating);
        var description = InspectorPresentation.CommandDescription(command, window);
        Assert.Contains(window.Id.ToString(), description);
        Assert.True(description.Length < 300);
        Assert.Contains("Untitled window", InspectorPresentation.CommandDescription(command, window with { Title = "" }));
        Assert.Contains(window.Id.ToString(), InspectorPresentation.CommandDescription(command, null));
        var other = window with { Id = Guid.NewGuid(), Title = "Not the command target" };
        var mismatched = InspectorPresentation.CommandDescription(command, other);
        Assert.Contains(window.Id.ToString(), mismatched);
        Assert.DoesNotContain(other.Title, mismatched);
        Assert.DoesNotContain(other.Id.ToString(), mismatched);
    }

    [Fact]
    public void CommandsAlwaysUseExplicitSelectedTargetsRatherThanNativeFocus()
    {
        var window = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var monitor = Guid.NewGuid();

        Assert.Equal(new(CommandKind.ActivateWorkspace, WorkspaceId: workspace, MonitorId: monitor),
            InspectorCommands.Activate(workspace, monitor));
        Assert.Equal(new(CommandKind.MoveToWorkspace, WindowId: window, WorkspaceId: workspace),
            InspectorCommands.MoveToWorkspace(window, workspace));
        Assert.Equal(new(CommandKind.Focus, WindowId: window), InspectorCommands.Focus(window));
        Assert.Equal(new(CommandKind.SetMode, WindowId: window, Mode: WindowMode.Tiling),
            InspectorCommands.SetMode(window, WindowMode.Tiling));
        Assert.Equal(new(CommandKind.SetMode, WindowId: window, Mode: WindowMode.Floating),
            InspectorCommands.SetMode(window, WindowMode.Floating));
    }

    [Fact]
    public void MissingSelectionsCannotFallBackToTheInspectorOrFocusedWindow()
    {
        Assert.Throws<InvalidOperationException>(() => InspectorCommands.Activate(null, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => InspectorCommands.Activate(Guid.NewGuid(), null));
        Assert.Throws<InvalidOperationException>(() => InspectorCommands.MoveToWorkspace(null, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => InspectorCommands.MoveToWorkspace(Guid.NewGuid(), null));
        Assert.Throws<InvalidOperationException>(() => InspectorCommands.Focus(null));
        Assert.Throws<InvalidOperationException>(() => InspectorCommands.SetMode(Guid.Empty, WindowMode.Tiling));
    }

    [Fact]
    public void ViewMapUsesTheAuthoritativeAssignmentsAndFallsBackOnlyWhenAbsent()
    {
        var snapshot = DemoSnapshot.Create();
        Assert.Equal(2, InspectorPresentation.ViewMap(snapshot).Count);
        var legacy = snapshot with { WorkspaceViews = null };
        Assert.Equal(InspectorPresentation.ViewMap(snapshot), InspectorPresentation.ViewMap(legacy));
        Assert.Empty(InspectorPresentation.ViewMap(snapshot with { WorkspaceViews = [] }));
        Assert.Contains(snapshot.Workspaces!, workspace => !workspace.Active);
        Assert.Contains(snapshot.Workspaces!, workspace => workspace.Active && workspace.WindowIds.Count == 0);
    }

    [Fact]
    public void NativeExclusionFlagsRemainVisibleWhenTilingIsExplicitlyOverridden()
    {
        var window = DemoSnapshot.Create().Desktop.Windows.Single(window => window.Fullscreen);
        Assert.Contains("default ignored: fullscreen", InspectorPresentation.WindowState(window));
        var overridden = InspectorPresentation.WindowState(window with { TilingOverride = true });
        Assert.Contains("native flags: fullscreen", overridden);
        Assert.Contains("tiling override", overridden);
        Assert.DoesNotContain("default ignored", overridden);
    }
}