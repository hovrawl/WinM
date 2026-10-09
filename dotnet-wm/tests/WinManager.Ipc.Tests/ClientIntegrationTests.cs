using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class ClientIntegrationTests
{
    [Fact]
    public async Task SdkDemultiplexesSnapshotCommandsAndEventsAndUnsubscribes()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await WinManagerClient.ConnectAsync(host.Name, cancellationToken: host.Token);
        var before = await client.QuerySnapshotAsync(host.Token);
        await using var subscription = await client.SubscribeAsync([ManagerEventTypes.PauseChanged], host.Token);
        await using var events = subscription.ReadEventsAsync(host.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ManagerEventTypes.Snapshot, events.Current.EventType);
        Assert.Equal(before.InstanceId, events.Current.InstanceId);
        var firstSequence = events.Current.Sequence;
        var result = await client.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token);
        Assert.True(result.Success);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ManagerEventTypes.PauseChanged, events.Current.EventType);
        Assert.True(events.Current.Sequence > firstSequence);
        Assert.Equal(result.StateRevision, events.Current.StateRevision);
        Assert.True(events.Current.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
        Assert.True((await client.QuerySnapshotAsync(host.Token)).Paused);
        await subscription.UnsubscribeAsync(host.Token);
        Assert.False(await events.MoveNextAsync());
        Assert.True((await client.ExecuteAsync(new(CommandKind.Pause, Paused: false), host.Token)).Success);
        Assert.False((await client.QuerySnapshotAsync(host.Token)).Paused);
    }

    [Fact]
    public async Task SdkWatchAcceptsEventConstantsAndFiltersEvents()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await WinManagerClient.ConnectAsync(host.Name, cancellationToken: host.Token);
        await using var events = WinManagerClient.WatchAsync(
            eventTypes: [ManagerEventTypes.PauseChanged], pipeName: host.Name,
            cancellationToken: host.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ManagerEventTypes.Snapshot, events.Current.EventType);
        Assert.True((await client.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token)).Success);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ManagerEventTypes.PauseChanged, events.Current.EventType);
        Assert.True(events.Current.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
    }

    [Fact]
    public async Task SdkErrorsAreStructuredAndDoNotBreakConnection()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await WinManagerClient.ConnectAsync(host.Name, cancellationToken: host.Token);
        var invalid = await Assert.ThrowsAsync<WinManagerRemoteException>(() =>
            client.ExecuteAsync(new(CommandKind.Pause), host.Token));
        Assert.Equal("invalid_argument", invalid.ErrorCode);
        var notFound = await Assert.ThrowsAsync<WinManagerRemoteException>(() =>
            client.ExecuteAsync(new(CommandKind.Focus, Guid.NewGuid()), host.Token));
        Assert.Equal("not_found", notFound.ErrorCode);
        Assert.Equal(host.Engine.Current!.StateRevision, (await client.QuerySnapshotAsync(host.Token)).StateRevision);
    }

    [Fact]
    public async Task SdkFreshConnectionAfterRestartUsesNewInstance()
    {
        var name = "WinManager.test." + Guid.NewGuid().ToString("N");
        Guid instance;
        await using (var host = await PipeHost.StartAsync(name))
        {
            await using var client = await WinManagerClient.ConnectAsync(name, cancellationToken: host.Token);
            instance = (await client.QuerySnapshotAsync(host.Token)).InstanceId;
        }
        await using var next = await PipeHost.StartAsync(name);
        await using var reconnected = await WinManagerClient.ConnectAsync(name, cancellationToken: next.Token);
        await using var subscription = await reconnected.SubscribeAsync(cancellationToken: next.Token);
        await using var events = subscription.ReadEventsAsync(next.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("snapshot", events.Current.EventType);
        Assert.NotEqual(instance, events.Current.InstanceId);
    }
}