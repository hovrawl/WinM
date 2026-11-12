using System.Threading.Channels;
using HomeDesk.Contracts;
using HomeDesk.Ui;
using HomeDeskManager;
using Xunit;

namespace HomeDesk.Ipc.Tests;

public sealed class ManagerSessionTests
{
    [Fact]
    public async Task ManagerPresenceIsOptInAndOwnedByTheSessionConnection()
    {
        await using var host = await PipeHost.StartAsync();
        await using var ordinary = await ManagerSession.ConnectAsync(host.Name, host.Token);
        Assert.True(host.TrayVisibility.IsVisible);

        await using var firstManager =
            await ManagerSession.ConnectAsync(host.Name, host.Token, registerManagerPresence: true);
        await using var secondManager =
            await ManagerSession.ConnectAsync(host.Name, host.Token, registerManagerPresence: true);
        Assert.False(host.TrayVisibility.IsVisible);

        await firstManager.DisposeAsync();
        Assert.False(host.TrayVisibility.IsVisible);
        await secondManager.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!host.TrayVisibility.IsVisible) await Task.Delay(10, timeout.Token);
        Assert.True(host.TrayVisibility.IsVisible);
    }

    [Fact]
    public async Task ManagerStartedBeforeHostConnectsWhenHostStartsLater()
    {
        var pipeName = "HomeDesk.test." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var delays = Channel.CreateUnbounded<TaskCompletionSource<bool>>();
        var initialFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<ManagerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        ManagerSession? session = null;
        var attempts = 0;
        var retry = ConnectionRetry.RunAsync(async token =>
        {
            attempts++;
            try
            {
                session = await ManagerSession.ConnectAsync(pipeName, token, registerManagerPresence: true);
                connected.TrySetResult(session);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                initialFailure.TrySetResult(error);
            }
        }, () => session?.IsConnected == true, TimeSpan.FromSeconds(1), (interval, token) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(1), interval);
            var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            delays.Writer.TryWrite(wait);
            return wait.Task.WaitAsync(token);
        }, cancellation.Token);

        try
        {
            var firstDelay = await delays.Reader.ReadAsync(cancellation.Token);
            Assert.Equal(1, attempts);
            Assert.NotNull(await initialFailure.Task.WaitAsync(cancellation.Token));
            Assert.False(connected.Task.IsCompleted);

            await using var host = await PipeHost.StartAsync(pipeName, workspaceDefinitions: [new("One")]);
            firstDelay.TrySetResult(true);

            var connectedSession = await connected.Task.WaitAsync(host.Token);
            var initial = new TaskCompletionSource<ManagerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            connectedSession.Start((snapshot, eventType) =>
            {
                if (eventType == ManagerEventTypes.Snapshot) initial.TrySetResult(snapshot);
            }, _ => { }, host.Token);

            var snapshot = await initial.Task.WaitAsync(host.Token);
            var workspace = Assert.Single(snapshot.Workspaces!);
            var position = new DeskPosition(240, -360);
            var result = await connectedSession.ExecuteAsync(new(CommandKind.SetWorkspacePosition,
                WorkspaceId: workspace.Id, Position: position), host.Token);

            Assert.True(result.Success);
            Assert.Equal(position, Assert.Single(host.Engine.Current!.Workspaces!).Position);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await retry;
            }
            catch (OperationCanceledException)
            {
            }

            if (session is not null) await session.DisposeAsync();
        }
    }

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
            if (snapshot.Workspaces?.Any(candidate => candidate.Id == workspace.Id && candidate.Position == position) ==
                true)
                changed.TrySetResult(snapshot);
        }, _ => { }, host.Token);

        Assert.Equal(workspace.Id, Assert.Single((await initial.Task.WaitAsync(host.Token)).Workspaces!).Id);
        var result = await session.ExecuteAsync(new(CommandKind.SetWorkspacePosition,
            WorkspaceId: workspace.Id, Position: position), host.Token);

        Assert.True(result.Success);
        Assert.Equal(position, Assert.Single((await changed.Task.WaitAsync(host.Token)).Workspaces!).Position);
    }
}