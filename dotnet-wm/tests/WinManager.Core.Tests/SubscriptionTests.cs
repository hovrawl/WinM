using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class SubscriptionTests
{
    [Fact]
    public async Task SnapshotPrecedesFilteredEventsAndRevisionSequenceAreMonotonic()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        var sink = new Sink();
        var subscribed = await engine.SubscribeAsync(sink, ["pause.changed"], timeout.Token);
        Assert.True(subscribed.Success);
        Assert.Equal("snapshot", Assert.Single(sink.Events).EventType);
        await engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), timeout.Token);
        Assert.Equal(new[] { "snapshot", "pause.changed" }, sink.Events.Select(envelope => envelope.EventType));
        Assert.True(sink.Events[1].Sequence > sink.Events[0].Sequence);
        Assert.True(sink.Events[1].StateRevision > sink.Events[0].StateRevision);
        Assert.Equal(sink.Events[0].InstanceId, sink.Events[1].InstanceId);
        await engine.UnsubscribeAsync(subscribed.SubscriptionId!.Value, timeout.Token);
        await engine.ExecuteAsync(new(CommandKind.Pause, Paused: false), timeout.Token);
        Assert.Equal(2, sink.Events.Count);
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
    }

    [Fact]
    public async Task SlowSubscriberIsDisconnectedWithoutBlockingHealthySubscriberOrCommands()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = engine.RunAsync(timeout.Token);
        var slow = new Sink { Limit = 1 };
        var healthy = new Sink();
        await engine.SubscribeAsync(slow, cancellationToken: timeout.Token);
        await engine.SubscribeAsync(healthy, cancellationToken: timeout.Token);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), timeout.Token)).Success);
        Assert.True(slow.Disconnected);
        Assert.Contains(healthy.Events, envelope => envelope.EventType == "pause.changed");
        await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
        await run;
        Assert.Equal("application.exiting", healthy.Events.Last().EventType);
    }

    [Fact]
    public async Task InvalidFiltersAreRejectedAndNewManagerHasFreshInstance()
    {
        var instances = new List<Guid>();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            await using var fake = new FakeWindowPlatform();
            var engine = new ManagerEngine(fake);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var run = engine.RunAsync(timeout.Token);
            var sink = new Sink();
            Assert.Equal("invalid_argument", (await engine.SubscribeAsync(sink, ["unknown"], timeout.Token)).ErrorCode);
            Assert.Empty(sink.Events);
            await engine.SubscribeAsync(sink, cancellationToken: timeout.Token);
            instances.Add(sink.Events[0].InstanceId);
            await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token);
            await run;
        }
        Assert.NotEqual(instances[0], instances[1]);
    }

    private sealed class Sink : IManagerEventSink
    {
        public List<EventEnvelope> Events { get; } = [];
        public int Limit { get; init; } = int.MaxValue;
        public bool Disconnected { get; private set; }
        public bool TryPublish(EventEnvelope envelope)
        {
            if (Events.Count >= Limit) return false;
            Events.Add(envelope);
            return true;
        }
        public void Disconnect(string reason) => Disconnected = true;
    }
}