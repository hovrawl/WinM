using WinManager.Contracts;
using WinManager.Ui;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class ManagerSessionTests
{
    [Fact]
    public async Task SharedSessionReceivesSnapshotAndPublishesPositionUpdates()
    {
        await using var host = await PipeHost.StartAsync(workspaceDefinitions: [new("One")]);
        await using var session = await ManagerSession.ConnectAsync(host.Name, host.Token);
        var initial = new TaskCompletionSource<ManagerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<ManagerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = Assert.Single(host.Engine.Current!.Workspaces!);
        var position = new DeskPosition(240, -360);

        session.Start((snapshot, eventType) =>
        {
            if (eventType == ManagerEventTypes.Snapshot) initial.TrySetResult(snapshot);
            if (snapshot.Workspaces?.Any(candidate => candidate.Id == workspace.Id && candidate.Position == position) == true)
                changed.TrySetResult(snapshot);
        }, _ => { }, host.Token);

        Assert.Equal(workspace.Id, Assert.Single((await initial.Task.WaitAsync(host.Token)).Workspaces!).Id);
        var result = await session.ExecuteAsync(new(CommandKind.SetWorkspacePosition,
            WorkspaceId: workspace.Id, Position: position), host.Token);

        Assert.True(result.Success);
        Assert.Equal(position, Assert.Single((await changed.Task.WaitAsync(host.Token)).Workspaces!).Position);
    }
}