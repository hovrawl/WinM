using System.IO.Pipes;
using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = deadline.Token;
await CorrelationAsync();
await SubscriptionOrderingAsync(false);
await SubscriptionOrderingAsync(true);
await LagAsync();
await ProtocolFailureAsync("version");
await ProtocolFailureAsync("kind");
await ProtocolFailureAsync("payload");
await ProtocolFailureAsync("first_event");
await CancellationAsync();
await ReconnectAsync();
await CliAsync();
await ThrowsAsync<TimeoutException>(() => WinManagerClient.ConnectAsync(Name(), TimeSpan.FromMilliseconds(50), token));
Console.WriteLine("PASS: concurrent correlation/serialized frames, early/late snapshot, unsubscribe/resubscribe, lag abort, version/kind/payload/order validation, cancellation, reconnect, CLI, timeout.");

string Name() => $"WinManager.Client.Validation.{Guid.NewGuid():N}";
NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
ManagerSnapshot Snapshot(Guid instance, long revision = 1) => new(instance, revision,
    new DesktopSnapshot([], [], null), new Dictionary<Guid, SplitNodeInfo?>(), false);
Task RespondAsync(NamedPipeServerStream server, RequestEnvelope request, object payload, long revision = 1) =>
    PipeFraming.WriteAsync(server, new ResponseEnvelope(Protocol.Version, request.RequestId, true, null, null,
        revision, Protocol.Payload(payload)), token);
Task PublishAsync(NamedPipeServerStream server, Guid instance, long sequence, long revision, string type = "snapshot") =>
    PipeFraming.WriteAsync(server, new EventEnvelope(Protocol.Version, instance, sequence, revision, type,
        Protocol.Payload(Snapshot(instance, revision))), token);
async Task<RequestEnvelope> RequestAsync(NamedPipeServerStream server)
{
    using var document = await PipeFraming.ReadAsync(server, token) ?? throw new Exception("Unexpected EOF.");
    return document.RootElement.Deserialize<RequestEnvelope>(Protocol.JsonOptions) ?? throw new Exception("Missing request.");
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

async Task CorrelationAsync()
{
    var name = Name();
    using var server = Server(name);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(token);
        var requests = new List<RequestEnvelope>();
        for (var i = 0; i < 24; i++) requests.Add(await RequestAsync(server));
        foreach (var request in requests.AsEnumerable().Reverse())
        {
            Check(request.Kind == "request" && request.ProtocolVersion == Protocol.Version, "Bad request envelope.");
            if (request.Operation == "query.snapshot") await RespondAsync(server, request, Snapshot(request.RequestId));
            else
            {
                Check(request.Operation == "command", "Unexpected operation.");
                var command = request.Payload.Deserialize<ManagerCommand>(Protocol.JsonOptions)!;
                await RespondAsync(server, request, new CommandResult(true, null, command.Amount!.Value.ToString(), 1));
            }
        }
    }, token);
    await using var client = await WinManagerClient.ConnectAsync(name, cancellationToken: token);
    var queries = Enumerable.Range(0, 12).Select(_ => client.QuerySnapshotAsync(token)).ToArray();
    var commands = Enumerable.Range(0, 12).Select(i => client.ExecuteAsync(new ManagerCommand(CommandKind.Resize, Amount: i), token)).ToArray();
    await Task.WhenAll(queries);
    await Task.WhenAll(commands);
    Check(queries.Select(q => q.Result.InstanceId).Distinct().Count() == 12, "Snapshot response correlation failed.");
    for (var i = 0; i < commands.Length; i++) Check(commands[i].Result.Message == i.ToString(), "Command response correlation failed.");
    await serverTask;
}

async Task SubscriptionOrderingAsync(bool responseFirst)
{
    var name = Name();
    using var server = Server(name);
    var id = Guid.NewGuid();
    var instance = Guid.NewGuid();
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(token);
        for (var round = 0; round < 2; round++)
        {
            var subscribe = await RequestAsync(server);
            Check(subscribe.Operation == "subscribe", "Expected subscribe.");
            var filter = subscribe.Payload.Deserialize<SubscribeRequest>(Protocol.JsonOptions)!;
            Check(filter.EventTypes!.SequenceEqual(["state"]), "Filter was not forwarded.");
            if (responseFirst) await RespondAsync(server, subscribe, new SubscriptionResult(true, id, null, 1));
            await PublishAsync(server, instance, 1, 1);
            await PublishAsync(server, instance, 2, 2, "state");
            if (!responseFirst) await RespondAsync(server, subscribe, new SubscriptionResult(true, id, null, 1));
            var query = await RequestAsync(server);
            await RespondAsync(server, query, Snapshot(instance, 2), 2);
            var unsubscribe = await RequestAsync(server);
            Check(unsubscribe.Operation == "unsubscribe" && unsubscribe.Payload.GetProperty("subscriptionId").GetGuid() == id, "Incorrect unsubscribe.");
            await RespondAsync(server, unsubscribe, new { success = true }, 2);
        }
    }, token);
    await using var client = await WinManagerClient.ConnectAsync(name, cancellationToken: token);
    for (var round = 0; round < 2; round++)
    {
        await using var subscription = await client.SubscribeAsync(["state"], token);
        Check(subscription.SubscriptionId == id && subscription.InstanceId == instance, "Subscription metadata missing.");
        await using var events = subscription.ReadEventsAsync(token).GetAsyncEnumerator(token);
        Check(await events.MoveNextAsync() && events.Current.EventType == "snapshot", "Initial snapshot not first.");
        Check(await events.MoveNextAsync() && events.Current.StateRevision == 2, "State event not second.");
        Check((await client.QuerySnapshotAsync(token)).StateRevision == 2, "Events interfered with responses.");
        await subscription.UnsubscribeAsync(token);
        Check(!await events.MoveNextAsync(), "Unsubscribe did not end stream.");
    }
    await serverTask;
}

async Task LagAsync()
{
    var name = Name();
    using var server = Server(name);
    var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(token);
        var request = await RequestAsync(server);
        var instance = Guid.NewGuid();
        await PublishAsync(server, instance, 1, 1);
        await RespondAsync(server, request, new SubscriptionResult(true, Guid.NewGuid(), null, 1));
        await publish.Task.WaitAsync(token);
        var query = await RequestAsync(server);
        await PublishAsync(server, instance, 2, 2, "state");
        using var eof = await PipeFraming.ReadAsync(server, token);
        Check(eof is null, "Lag did not terminate the connection.");
    }, token);
    await using var client = await WinManagerClient.ConnectAsync(name, cancellationToken: token, eventQueueCapacity: 1);
    await using var subscription = await client.SubscribeAsync(cancellationToken: token);
    publish.SetResult();
    await ThrowsAsync<WinManagerConsumerLagException>(() => client.QuerySnapshotAsync(token));
    await using var events = subscription.ReadEventsAsync(token).GetAsyncEnumerator(token);
    Check(await events.MoveNextAsync() && events.Current.EventType == "snapshot", "Buffered snapshot was lost.");
    await ThrowsAsync<WinManagerConsumerLagException>(async () => { await events.MoveNextAsync(); });
    Check(!client.IsConnected, "Lagging connection still connected.");
    await serverTask;
}

async Task ProtocolFailureAsync(string failure)
{
    var name = Name();
    using var server = Server(name);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(token);
        var request = await RequestAsync(server);
        if (failure == "first_event") await PublishAsync(server, Guid.NewGuid(), 1, 1, "state");
        else await PipeFraming.WriteAsync(server, new ResponseEnvelope(failure == "version" ? 99 : Protocol.Version,
            request.RequestId, true, null, null, 1, Protocol.Payload(new { bad = "payload" }),
            failure == "kind" ? "request" : "response"), token);
    }, token);
    await using var client = await WinManagerClient.ConnectAsync(name, cancellationToken: token);
    await ThrowsAsync<WinManagerProtocolException>(async () =>
    {
        if (failure == "first_event") await client.SubscribeAsync(cancellationToken: token);
        else await client.QuerySnapshotAsync(token);
    });
    Check(!client.IsConnected, "Malformed protocol did not terminate connection.");
    await serverTask;
}

async Task CancellationAsync()
{
    var name = Name();
    using var server = Server(name);
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(token);
        await RequestAsync(server);
        cancellation.Cancel();
        using var eof = await PipeFraming.ReadAsync(server, token);
        Check(eof is null, "Cancelled request did not close connection.");
    }, token);
    await using var client = await WinManagerClient.ConnectAsync(name, cancellationToken: token);
    await ThrowsAsync<OperationCanceledException>(() => client.QuerySnapshotAsync(cancellation.Token));
    await serverTask;
}

async Task ReconnectAsync()
{
    var name = Name();
    var instances = new[] { Guid.NewGuid(), Guid.NewGuid() };
    using var first = Server(name);
    var serverTask = Task.Run(async () =>
    {
        await first.WaitForConnectionAsync(token);
        var request = await RequestAsync(first);
        await PublishAsync(first, instances[0], 1, 1);
        await RespondAsync(first, request, new SubscriptionResult(true, Guid.NewGuid(), null, 1));
        first.Dispose();
        using var second = Server(name);
        await second.WaitForConnectionAsync(token);
        request = await RequestAsync(second);
        await PublishAsync(second, instances[1], 1, 1);
        await RespondAsync(second, request, new SubscriptionResult(true, Guid.NewGuid(), null, 1));
        using var eof = await PipeFraming.ReadAsync(second, token);
        Check(eof is null, "Watch disposal did not close connection.");
    }, token);
    await using (var events = WinManagerClient.WatchAsync(pipeName: name, reconnect: true,
        reconnectDelay: TimeSpan.FromMilliseconds(10), cancellationToken: token).GetAsyncEnumerator(token))
    {
        for (var i = 0; i < 2; i++)
            Check(await events.MoveNextAsync() && events.Current.EventType == "snapshot" && events.Current.InstanceId == instances[i],
                "Reconnect failed to deliver a fresh snapshot and instance ID.");
    }
    await serverTask;
}

async Task CliAsync()
{
    var originalOut = Console.Out;
    var originalError = Console.Error;
    using var output = new StringWriter();
    using var errors = new StringWriter();
    Console.SetOut(output);
    Console.SetError(errors);
    try
    {
        Check(await WinManager.Cli.Program.Main(["--help"]) == 0, "Help failed.");
        Check(await WinManager.Cli.Program.Main(["command", "{} "]) == 2, "Missing command kind accepted.");
        Check(await WinManager.Cli.Program.Main(["command", "{\"kind\":\"redraw\",\"shell\":\"bad\"}"]) == 2, "Unknown JSON member accepted.");
        Check(await WinManager.Cli.Program.Main(["watch", "--event-types", "state,,snapshot"]) == 2, "Invalid event filter accepted.");
        var name = Name();
        using var server = Server(name);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(token);
            var request = await RequestAsync(server);
            var command = request.Payload.Deserialize<ManagerCommand>(Protocol.JsonOptions)!;
            Check(command.Kind == CommandKind.Pause && command.Paused == true, "CLI JSON command was corrupted.");
            await RespondAsync(server, request, new CommandResult(false, "invalid_argument", "test", 1));
        }, token);
        Check(await WinManager.Cli.Program.Main(["command", "{\"kind\":\"pause\",\"paused\":true}", "--pipe", name]) == 3, "CLI rejection exit code wrong.");
        await serverTask;
    }
    finally { Console.SetOut(originalOut); Console.SetError(originalError); }
}