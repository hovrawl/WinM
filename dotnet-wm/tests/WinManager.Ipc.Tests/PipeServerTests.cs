using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WinManager.Contracts;
using WinManager.Core;
using WinManager.Host;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class PipeServerTests
{
    [Fact]
    public async Task ManagerAdmissionOverloadReturnsBusyAndSnapshotIsNotReadyBeforeStartup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var platform = new TestWindowPlatform(8);
        var engine = new ManagerEngine(platform, capacity: 1);
        var admitted = engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
        var name = "WinManager.test." + Guid.NewGuid().ToString("N");
        await using var server = new PipeServer(engine, name);
        var serverRun = server.RunAsync(timeout.Token);
        await using var client = PipeHost.CreateClient(name);
        await client.ConnectAsync(timeout.Token);
        await Send(client, Request("command", new ManagerCommand(CommandKind.Pause, Paused: true)), timeout.Token);
        var busy = await Response(client, timeout.Token);
        Assert.False(busy.Success);
        Assert.Equal("busy", busy.ErrorCode);
        await Send(client, Request("query.snapshot", new { }), timeout.Token);
        Assert.Equal("not_ready", (await Response(client, timeout.Token)).ErrorCode);
        var engineRun = engine.RunAsync(timeout.Token);
        Assert.True((await admitted).Success);
        Assert.False(engine.Current!.Paused);
        await Send(client, Request("query.snapshot", new { }), timeout.Token);
        Assert.True((await Response(client, timeout.Token)).Success);
        await server.DisposeAsync();
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Exit), timeout.Token)).Success);
        await engineRun.WaitAsync(timeout.Token);
        await serverRun.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ClientWithoutImpersonationIsRejectedAndCannotExecuteCommand()
    {
        await using var host = await PipeHost.StartAsync(maximumClients: 1);
        await using var anonymous = new NamedPipeClientStream(".", host.Name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try
        {
            await anonymous.ConnectAsync(host.Token);
            await Send(anonymous, Request("command", new ManagerCommand(CommandKind.Pause, Paused: true)), host.Token);
            await Disconnected(anonymous, host.Token);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        Assert.False(host.Engine.Current!.Paused);
        await using var permitted = await host.ConnectAsync();
        await Send(permitted, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(permitted, host.Token)).Success);
    }

    [Fact]
    public async Task PartialPrefixAndPayloadProduceCorrelatedSnapshot()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var request = Request("query.snapshot", new { });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Protocol.JsonOptions);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        foreach (var value in prefix.Concat(bytes)) await client.WriteAsync(new byte[] { value }, host.Token);
        var response = await Response(client, host.Token);
        Assert.True(response.Success);
        Assert.Equal(request.RequestId, response.RequestId);
        var snapshot = response.Payload!.Value.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;
        Assert.Equal(host.Engine.Current!.InstanceId, snapshot.InstanceId);
        Assert.Equal(snapshot.StateRevision, response.StateRevision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Protocol.MaximumFrameBytes + 1)]
    public async Task InvalidLengthDisconnectsOnlyOffendingClient(int length)
    {
        await using var host = await PipeHost.StartAsync(maximumClients: 1);
        await using var client = await host.ConnectAsync();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await client.WriteAsync(prefix, host.Token);
        await Disconnected(client, host.Token);
        await using var replacement = await host.ConnectAsync();
        await Send(replacement, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(replacement, host.Token)).Success);
    }

    [Fact]
    public async Task MaximumFrameLengthIsAcceptedAndExcessiveJsonDepthDisconnects()
    {
        await using var host = await PipeHost.StartAsync(maximumClients: 1);
        await using var client = await host.ConnectAsync();
        var id = Guid.NewGuid();
        var beginning = $"{{\"protocolVersion\":1,\"kind\":\"request\",\"requestId\":\"{id}\",\"operation\":\"query.snapshot\",\"payload\":{{\"padding\":\"";
        const string ending = "\"}}";
        var json = beginning + new string('x', Protocol.MaximumFrameBytes - Encoding.UTF8.GetByteCount(beginning + ending)) + ending;
        Assert.Equal(Protocol.MaximumFrameBytes, Encoding.UTF8.GetByteCount(json));
        await WriteJson(client, json, host.Token);
        var response = await Response(client, host.Token);
        Assert.Equal(id, response.RequestId);
        Assert.Equal("invalid_argument", response.ErrorCode);
        await WriteJson(client, new string('[', 65) + "0" + new string(']', 65), host.Token);
        await Disconnected(client, host.Token);
    }

    [Fact]
    public async Task TruncatedAndMalformedJsonDoNotStopListener()
    {
        await using var host = await PipeHost.StartAsync(maximumClients: 1);
        await using (var truncated = await host.ConnectAsync())
            await truncated.WriteAsync(new byte[] { 10, 0 }, host.Token);
        await using (var malformed = await host.ConnectAsync())
        {
            await WriteJson(malformed, "{", host.Token);
            await Disconnected(malformed, host.Token);
        }
        await using var next = await host.ConnectAsync();
        await Send(next, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(next, host.Token)).Success);
    }

    [Theory]
    [InlineData("\"protocolVersion\":2,\"kind\":\"request\",\"operation\":\"query.snapshot\",\"payload\":{}", "unsupported_version")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"event\",\"operation\":\"query.snapshot\",\"payload\":{}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"unknown\",\"payload\":{}", "unknown_operation")]
    [InlineData("\"protocolVersion\":\"1\",\"kind\":\"request\",\"operation\":\"query.snapshot\",\"payload\":{}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"query.snapshot\",\"payload\":{\"extra\":1}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"command\",\"payload\":{}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"command\",\"payload\":{\"kind\":100}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"command\",\"payload\":{\"kind\":\"unknown\"}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"command\",\"payload\":{\"kind\":\"pause\"}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"subscribe\",\"payload\":{\"eventTypes\":[\"unknown\"]}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"unsubscribe\",\"payload\":{\"subscriptionId\":\"bad\"}", "invalid_argument")]
    [InlineData("\"protocolVersion\":1,\"protocolVersion\":1,\"kind\":\"request\",\"operation\":\"query.snapshot\",\"payload\":{}", "invalid_argument")]
    public async Task InvalidRequestsReturnStructuredCorrelatedErrors(string properties, string code)
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var id = Guid.NewGuid();
        var revision = host.Engine.Current!.StateRevision;
        await WriteJson(client, $"{{\"requestId\":\"{id}\",{properties}}}", host.Token);
        var response = await Response(client, host.Token);
        Assert.Equal(id, response.RequestId);
        Assert.False(response.Success);
        Assert.Equal(code, response.ErrorCode);
        Assert.Equal(revision, host.Engine.Current.StateRevision);
        await Send(client, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(client, host.Token)).Success);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"bad\"")]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    public async Task InvalidRequestIdReturnsUncorrelatedErrorWithoutKillingConnection(string requestId)
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await host.ConnectAsync();
        await WriteJson(client, $"{{\"protocolVersion\":1,\"kind\":\"request\",\"requestId\":{requestId},\"operation\":\"query.snapshot\",\"payload\":{{}}}}", host.Token);
        var response = await Response(client, host.Token);
        Assert.Equal(Guid.Empty, response.RequestId);
        Assert.Equal("invalid_argument", response.ErrorCode);
    }

    [Fact]
    public async Task SubscribeSnapshotPrecedesResponseAndCommandsInterleaveCommittedEvents()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var initial = await Subscribe(client, host.Token);
        var request = Request("command", new ManagerCommand(CommandKind.Pause, Paused: true));
        await Send(client, request, host.Token);
        var events = new List<EventEnvelope>();
        ResponseEnvelope response;
        while (true)
        {
            var message = await Read(client, host.Token);
            if (message.GetProperty("kind").GetString() == "event") events.Add(message.Deserialize<EventEnvelope>(Protocol.JsonOptions)!);
            else
            {
                response = message.Deserialize<ResponseEnvelope>(Protocol.JsonOptions)!;
                break;
            }
        }
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.True(response.Success);
        var changed = Assert.Single(events, envelope => envelope.EventType == "pause.changed");
        Assert.True(changed.Sequence > initial.Event.Sequence);
        Assert.Equal(initial.Event.InstanceId, changed.InstanceId);
        Assert.Equal(response.StateRevision, changed.StateRevision);
        var snapshot = changed.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;
        Assert.True(snapshot.Paused);
        Assert.Equal(changed.StateRevision, snapshot.StateRevision);
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public async Task MultipleSubscribersShareGlobalEventsAndFiltersStillReceiveInitialSnapshot()
    {
        await using var host = await PipeHost.StartAsync();
        await using var first = await host.ConnectAsync();
        await using var second = await host.ConnectAsync();
        var a = await Subscribe(first, host.Token, ["pause.changed"]);
        var b = await Subscribe(second, host.Token, ["pause.changed"]);
        Assert.Equal(a.Event.InstanceId, b.Event.InstanceId);
        Assert.True(b.Event.Sequence > a.Event.Sequence);
        Assert.Equal(a.Event.StateRevision, b.Event.StateRevision);
        Assert.True((await host.Engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token)).Success);
        var eventA = (await Read(first, host.Token)).Deserialize<EventEnvelope>(Protocol.JsonOptions)!;
        var eventB = (await Read(second, host.Token)).Deserialize<EventEnvelope>(Protocol.JsonOptions)!;
        Assert.Equal("pause.changed", eventA.EventType);
        Assert.Equal(eventA.Sequence, eventB.Sequence);
        Assert.Equal(eventA.Payload.GetRawText(), eventB.Payload.GetRawText());
    }

    [Fact]
    public async Task RegistrationIsAtomicWithConcurrentChanges()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var subscription = Request("subscribe", new SubscribeRequest(["pause.changed"]));
        await Send(client, subscription, host.Token);
        var changes = Task.Run(async () =>
        {
            for (var index = 0; index < 12; index++)
                Assert.True((await host.Engine.ExecuteAsync(new(CommandKind.Pause, Paused: index % 2 == 0), host.Token)).Success);
        });
        var first = (await Read(client, host.Token)).Deserialize<EventEnvelope>(Protocol.JsonOptions)!;
        Assert.Equal("snapshot", first.EventType);
        var events = new List<EventEnvelope> { first };
        var responseSeen = false;
        await changes;
        var barrier = Request("query.snapshot", new { });
        await Send(client, barrier, host.Token);
        while (true)
        {
            var message = await Read(client, host.Token);
            if (message.GetProperty("kind").GetString() == "event") events.Add(message.Deserialize<EventEnvelope>(Protocol.JsonOptions)!);
            else
            {
                var response = message.Deserialize<ResponseEnvelope>(Protocol.JsonOptions)!;
                Assert.True(response.Success);
                if (response.RequestId == barrier.RequestId)
                {
                    Assert.True(responseSeen);
                    Assert.Equal(response.StateRevision, events[^1].StateRevision);
                    break;
                }
                Assert.Equal(subscription.RequestId, response.RequestId);
                responseSeen = true;
            }
        }
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence && pair.First.StateRevision < pair.Second.StateRevision));
        foreach (var envelope in events)
        {
            var snapshot = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!;
            Assert.Equal(envelope.StateRevision, snapshot.StateRevision);
            Assert.Equal(envelope.InstanceId, snapshot.InstanceId);
        }
    }

    [Fact]
    public async Task UnsubscribeIsOwnedAndStopsEventsBeforeAcknowledgement()
    {
        await using var host = await PipeHost.StartAsync();
        await using var owner = await host.ConnectAsync();
        await using var other = await host.ConnectAsync();
        var subscribed = await Subscribe(owner, host.Token, ["pause.changed"]);
        await Send(other, Request("unsubscribe", new { subscriptionId = subscribed.Id }), host.Token);
        Assert.Equal("not_found", (await Response(other, host.Token)).ErrorCode);
        await Send(owner, Request("subscribe", new SubscribeRequest()), host.Token);
        Assert.Equal("already_subscribed", (await Response(owner, host.Token)).ErrorCode);
        await Send(owner, Request("unsubscribe", new { subscriptionId = subscribed.Id }), host.Token);
        Assert.True((await Response(owner, host.Token)).Success);
        await host.Engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token);
        await Send(owner, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(owner, host.Token)).Success);
        Assert.Equal("snapshot", (await Subscribe(owner, host.Token)).Event.EventType);
    }

    [Fact]
    public async Task ClientLimitIsBoundedAndSlotIsReusedAfterDisconnect()
    {
        await using var host = await PipeHost.StartAsync(maximumClients: 1);
        var first = await host.ConnectAsync();
        await Send(first, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(first, host.Token)).Success);
        await using var extra = PipeHost.CreateClient(host.Name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extra.ConnectAsync(timeout.Token));
        first.Dispose();
        await using var next = await host.ConnectAsync();
        await Send(next, Request("query.snapshot", new { }), host.Token);
        Assert.True((await Response(next, host.Token)).Success);
    }

    [Fact]
    public async Task SlowSubscriberDisconnectsWithoutBlockingManagerOrHealthyClient()
    {
        await using var host = await PipeHost.StartAsync(outputCapacity: 2, titleLength: 128_000);
        await using var slow = await host.ConnectAsync();
        await Subscribe(slow, host.Token, ["pause.changed"]);
        await using var healthy = await host.ConnectAsync();
        await Subscribe(healthy, host.Token, ["pause.changed"]);
        long previous = 0;
        for (var index = 0; index < 24; index++)
        {
            Assert.True((await host.Engine.ExecuteAsync(new(CommandKind.Pause, Paused: index % 2 == 0), host.Token)).Success);
            var changed = (await Read(healthy, host.Token)).Deserialize<EventEnvelope>(Protocol.JsonOptions)!;
            Assert.Equal("pause.changed", changed.EventType);
            Assert.True(changed.Sequence > previous);
            previous = changed.Sequence;
        }
        await Disconnected(slow, host.Token);
        await using var replacement = await host.ConnectAsync();
        var fresh = await Subscribe(replacement, host.Token, ["pause.changed"]);
        Assert.Equal(host.Engine.Current!.StateRevision, fresh.Event.StateRevision);
        Assert.True(fresh.Event.Sequence > previous);
    }

    [Fact]
    public async Task ExitEventContainsFinalCommittedSnapshotAndServerDrainsOnShutdown()
    {
        await using var host = await PipeHost.StartAsync();
        await using var client = await host.ConnectAsync();
        await Subscribe(client, host.Token, ["application.exiting"]);
        Assert.True((await host.Engine.ExecuteAsync(new(CommandKind.Exit), host.Token)).Success);
        await host.EngineRun.WaitAsync(host.Token);
        await host.Server.DisposeAsync();
        var exiting = (await Read(client, host.Token)).Deserialize<EventEnvelope>(Protocol.JsonOptions)!;
        Assert.Equal("application.exiting", exiting.EventType);
        Assert.Equal(host.Engine.Current!.StateRevision, exiting.StateRevision);
        Assert.Equal(host.Engine.Current.InstanceId, exiting.InstanceId);
        await Disconnected(client, host.Token);
    }

    [Fact]
    public async Task ReconnectAfterRestartGetsNewInstanceWithoutReplay()
    {
        var name = "WinManager.test." + Guid.NewGuid().ToString("N");
        Guid previousInstance;
        await using (var host = await PipeHost.StartAsync(name: name))
        {
            await using var client = await host.ConnectAsync();
            var initial = await Subscribe(client, host.Token);
            previousInstance = initial.Event.InstanceId;
            await host.Engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token);
        }
        await using var replacement = await PipeHost.StartAsync(name: name);
        await using var next = await replacement.ConnectAsync();
        var fresh = await Subscribe(next, replacement.Token);
        Assert.NotEqual(previousInstance, fresh.Event.InstanceId);
        Assert.False(fresh.Event.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
    }

    private static RequestEnvelope Request<T>(string operation, T payload) =>
        new(Protocol.Version, Guid.NewGuid(), operation, Protocol.Payload(payload));
    private static Task Send(NamedPipeClientStream client, RequestEnvelope request, CancellationToken token) =>
        PipeFraming.WriteAsync(client, request, token);
    private static async Task WriteJson(Stream stream, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
    }
    private static async Task<JsonElement> Read(Stream client, CancellationToken token)
    {
        using var document = await PipeFraming.ReadAsync(client, token);
        Assert.NotNull(document);
        return document.RootElement.Clone();
    }
    private static async Task<ResponseEnvelope> Response(Stream client, CancellationToken token)
    {
        var message = await Read(client, token);
        Assert.Equal("response", message.GetProperty("kind").GetString());
        return message.Deserialize<ResponseEnvelope>(Protocol.JsonOptions)!;
    }
    private static async Task<(EventEnvelope Event, Guid Id)> Subscribe(NamedPipeClientStream client, CancellationToken token, string[]? types = null)
    {
        var request = Request("subscribe", new SubscribeRequest(types));
        await Send(client, request, token);
        var first = await Read(client, token);
        Assert.Equal("event", first.GetProperty("kind").GetString());
        var initial = first.Deserialize<EventEnvelope>(Protocol.JsonOptions)!;
        Assert.Equal("snapshot", initial.EventType);
        var response = await Response(client, token);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.True(response.Success);
        var result = response.Payload!.Value.Deserialize<SubscriptionResult>(Protocol.JsonOptions)!;
        Assert.True(result.Success);
        return (initial, result.SubscriptionId!.Value);
    }
    private static async Task Disconnected(Stream client, CancellationToken token)
    {
        try
        {
            while (await PipeFraming.ReadAsync(client, token) is { } document) document.Dispose();
        }
        catch (IOException) { }
    }
}

internal sealed class PipeHost : IAsyncDisposable
{
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    private readonly TestWindowPlatform platform;
    public string Name { get; }
    public ManagerEngine Engine { get; }
    public PipeServer Server { get; }
    public Task EngineRun { get; }
    public Task ServerRun { get; }
    public CancellationToken Token => timeout.Token;

    private PipeHost(string? name, int maximumClients, int outputCapacity, int titleLength)
    {
        Name = name ?? "WinManager.test." + Guid.NewGuid().ToString("N");
        platform = new TestWindowPlatform(titleLength);
        Engine = new(platform);
        EngineRun = Engine.RunAsync(Token);
        Server = new(Engine, Name, maximumClients, outputCapacity);
        ServerRun = Server.RunAsync(Token);
    }
    internal static async Task<PipeHost> StartAsync(string? name = null, int maximumClients = 16, int outputCapacity = 128, int titleLength = 8)
    {
        var host = new PipeHost(name, maximumClients, outputCapacity, titleLength);
        Assert.True((await host.Engine.ExecuteAsync(new(CommandKind.Redraw), host.Token)).Success);
        return host;
    }
    internal static NamedPipeClientStream CreateClient(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
    internal async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = CreateClient(Name);
        await client.ConnectAsync(Token);
        return client;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.DisposeAsync();
            if (!EngineRun.IsCompleted) await Engine.ExecuteAsync(new(CommandKind.Exit), Token);
            await EngineRun.WaitAsync(Token);
            await ServerRun.WaitAsync(Token);
        }
        finally
        {
            await timeout.CancelAsync();
            await platform.DisposeAsync();
            timeout.Dispose();
        }
    }
}

internal sealed class TestWindowPlatform : IWindowPlatform
{
    private readonly Channel<NativeNotification> notifications = Channel.CreateBounded<NativeNotification>(1);
    private DesktopSnapshot desktop;
    internal TestWindowPlatform(int titleLength)
    {
        var monitor = Guid.NewGuid();
        desktop = new([new(monitor, "test", new(0, 0, 1000, 900), new(0, 0, 1000, 850), 96)],
            [new(Guid.NewGuid(), monitor, new string('x', titleLength), "test", "test", new(0, 0, 200, 200), true, false, false, WindowMode.Tiling)], null);
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<DesktopSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(desktop);
    public IAsyncEnumerable<NativeNotification> Notifications(CancellationToken cancellationToken) => notifications.Reader.ReadAllAsync(cancellationToken);
    public Task<ApplyResult> ApplyAsync(LayoutPlan plan, CancellationToken cancellationToken)
    {
        desktop = desktop with
        {
            Windows = desktop.Windows.Select(window => window with
            {
                Bounds = plan.Placements.FirstOrDefault(placement => placement.WindowId == window.Id)?.Bounds ?? window.Bounds,
                Visible = plan.Visibility?.FirstOrDefault(visibility => visibility.WindowId == window.Id)?.Visible ?? window.Visible
            }).ToArray(),
            FocusedWindowId = plan.FocusWindowId ?? desktop.FocusedWindowId
        };
        return Task.FromResult(new ApplyResult([]));
    }
    public Task<ApplyResult> RestoreAsync(CancellationToken cancellationToken) => Task.FromResult(new ApplyResult([]));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}