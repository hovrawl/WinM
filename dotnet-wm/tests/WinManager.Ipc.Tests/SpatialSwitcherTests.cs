using Avalonia;
using WinManager.Contracts;
using WinManager.SpatialSwitcher;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class SpatialSwitcherTests
{
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

    private static WorkspaceInfo Workspace(string name, DeskPosition? position) =>
        new(Guid.NewGuid(), name, Guid.Empty, false, [], WorkspaceLayout.Split, position);
}