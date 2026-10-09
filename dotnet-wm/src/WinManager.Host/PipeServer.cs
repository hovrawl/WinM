using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using WinManager.Contracts;
using WinManager.Core;
using WinManager.Windows;

namespace WinManager.Host;

public sealed class PipeServer : IAsyncDisposable
{
    private readonly ManagerEngine manager;
    private readonly string pipeName;
    private readonly int maximumClients;
    private readonly int outputCapacity;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private Task? run;
    private int started;
    private int disposed;

    public PipeServer(ManagerEngine manager, string? pipeName = null, int maximumClients = 16, int outputCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumClients, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumClients, 254);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputCapacity, 1);
        this.manager = manager;
        this.pipeName = pipeName ?? Protocol.DefaultPipeName();
        ArgumentException.ThrowIfNullOrWhiteSpace(this.pipeName);
        this.maximumClients = maximumClients;
        this.outputCapacity = outputCapacity;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local Windows IPC is required.");
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Server already started.");
        return run = AcceptAsync(cancellationToken);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        using var slots = new SemaphoreSlim(maximumClients);
        long nextClient = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await slots.WaitAsync(stop.Token);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = LocalPipeServer.Create(pipeName, maximumClients, 4096);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var id = ++nextClient;
                    var connection = new Connection(manager, pipe, outputCapacity, stop.Token);
                    pipe = null;
                    clients[id] = ServeAsync(connection, slots);
                    foreach (var finished in clients.Where(entry => entry.Value.IsCompletedSuccessfully))
                        clients.TryRemove(finished.Key, out _);
                }
                catch
                {
                    pipe?.Dispose();
                    slots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(clients.Values);
        }
    }

    private static async Task ServeAsync(Connection connection, SemaphoreSlim slots)
    {
        try { await connection.RunAsync(); }
        finally { slots.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await shutdown.CancelAsync();
        if (run is not null) await run;
        shutdown.Dispose();
    }

    private sealed class Connection : IManagerEventSink
    {
        private readonly ManagerEngine manager;
        private readonly NamedPipeServerStream pipe;
        private readonly Channel<object> output;
        private readonly CancellationTokenSource stop;
        private readonly CancellationTokenSource writerAbort = new();
        private Guid? subscriptionId;

        internal Connection(ManagerEngine manager, NamedPipeServerStream pipe, int capacity, CancellationToken cancellationToken)
        {
            this.manager = manager;
            this.pipe = pipe;
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            output = Channel.CreateBounded<object>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        public bool TryPublish(EventEnvelope envelope) => Enqueue(envelope);
        public void Disconnect(string reason)
        {
            _ = writerAbort.CancelAsync();
            _ = stop.CancelAsync();
        }

        private bool Enqueue(object message)
        {
            if (!stop.IsCancellationRequested && output.Writer.TryWrite(message)) return true;
            Disconnect("slow_consumer");
            return false;
        }

        internal async Task RunAsync()
        {
            Task writer = Task.CompletedTask;
            try
            {
                var validated = false;
                while (!stop.IsCancellationRequested)
                {
                    using var document = await PipeFraming.ReadAsync(pipe, stop.Token);
                    if (document is null) break;
                    if (!validated)
                    {
                        // ImpersonateNamedPipeClient requires data to have been read from the caller.
                        if (!PipeClientValidation.Validate(pipe)) return;
                        validated = true;
                        writer = WriteAsync();
                    }
                    await HandleAsync(document.RootElement);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or JsonException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { }
            finally
            {
                await stop.CancelAsync();
                output.Writer.TryComplete();
                try
                {
                    try { await writer.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException)
                    {
                        await writerAbort.CancelAsync();
                        await writer;
                    }
                }
                finally
                {
                    pipe.Dispose();
                    if (subscriptionId is { } id)
                    {
                        CommandResult result;
                        do
                        {
                            result = await manager.UnsubscribeAsync(id, CancellationToken.None);
                            if (result.ErrorCode == "busy") await Task.Delay(10);
                        } while (result.ErrorCode == "busy");
                    }
                    writerAbort.Dispose();
                    stop.Dispose();
                }
            }
        }

        private async Task WriteAsync()
        {
            try
            {
                await foreach (var message in output.Reader.ReadAllAsync(writerAbort.Token))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(writerAbort.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await PipeFraming.WriteAsync(pipe, message, timeout.Token);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                Disconnect("write_failed");
            }
        }

        private async Task HandleAsync(JsonElement root)
        {
            var requestId = Guid.Empty;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("requestId", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                idElement.TryGetGuid(out requestId);
            try
            {
                CheckObject(root, "protocolVersion", "requestId", "operation", "payload", "kind");
                if (!root.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var number))
                    throw new ArgumentException("protocolVersion is required.");
                if (number != Protocol.Version)
                {
                    Error(requestId, "unsupported_version", "Unsupported protocol version.");
                    return;
                }
                if (requestId == Guid.Empty || !root.TryGetProperty("kind", out var kind) || kind.GetString() != "request" ||
                    !root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("payload", out var payload))
                    throw new ArgumentException("A request requires kind, requestId, operation and payload.");
                switch (operation.GetString())
                {
                    case "query.snapshot":
                        if (payload.ValueKind != JsonValueKind.Null) CheckObject(payload);
                        var snapshot = manager.Current;
                        if (snapshot is null) Error(requestId, "not_ready", "Manager has no committed snapshot.");
                        else Respond(requestId, true, null, null, snapshot.StateRevision, Protocol.Payload(snapshot));
                        break;
                    case "command":
                        CheckObject(payload, "kind", "windowId", "direction", "amount", "orientation", "mode", "paused", "workspaceId", "monitorId", "workspaceName");
                        if (!payload.TryGetProperty("kind", out _)) throw new ArgumentException("Command kind is required.");
                        var command = payload.Deserialize<ManagerCommand>(Protocol.JsonOptions) ?? throw new ArgumentException("Command is required.");
                        var result = await manager.ExecuteAsync(command, stop.Token);
                        Respond(requestId, result.Success, result.ErrorCode, result.Message, result.StateRevision, Protocol.Payload(result));
                        break;
                    case "subscribe":
                        CheckObject(payload, "eventTypes");
                        var subscribe = payload.Deserialize<SubscribeRequest>(Protocol.JsonOptions)!;
                        if (subscribe.EventTypes is { } types && (types.Count > ManagerEngine.EventTypes.Count || types.Any(type => type is null || !ManagerEngine.EventTypes.Contains(type))))
                            throw new ArgumentException("Unknown event type or too many filters.");
                        if (subscriptionId is not null)
                        {
                            Error(requestId, "already_subscribed", "Only one subscription is allowed per connection.");
                            break;
                        }
                        // Do not abandon a successful actor registration before capturing its ID for cleanup.
                        var subscribed = await manager.SubscribeAsync(this, subscribe.EventTypes, stop.Token);
                        subscriptionId = subscribed.SubscriptionId;
                        Respond(requestId, subscribed.Success, subscribed.ErrorCode, null, subscribed.StateRevision, Protocol.Payload(subscribed));
                        break;
                    case "unsubscribe":
                        CheckObject(payload, "subscriptionId");
                        if (!payload.TryGetProperty("subscriptionId", out var subscription) || !subscription.TryGetGuid(out var requestedId) || requestedId == Guid.Empty)
                            throw new ArgumentException("A nonempty subscriptionId is required.");
                        if (subscriptionId != requestedId)
                        {
                            Error(requestId, "not_found", "Subscription does not belong to this connection.");
                            break;
                        }
                        var unsubscribed = await manager.UnsubscribeAsync(requestedId, stop.Token);
                        if (unsubscribed.Success) subscriptionId = null;
                        Respond(requestId, unsubscribed.Success, unsubscribed.ErrorCode, unsubscribed.Message, unsubscribed.StateRevision);
                        break;
                    default:
                        Error(requestId, "unknown_operation", "Unknown operation.");
                        break;
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                Error(requestId, "invalid_argument", "Invalid request envelope or operation arguments.");
            }
        }

        private static void CheckObject(JsonElement element, params string[] allowed)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                    throw new ArgumentException("Unknown or duplicate property.");
        }

        private void Error(Guid id, string code, string message) =>
            Respond(id, false, code, message, manager.Current?.StateRevision ?? 0);

        private void Respond(Guid id, bool success, string? code, string? message, long revision, JsonElement? payload = null) =>
            Enqueue(new ResponseEnvelope(Protocol.Version, id, success, code, message, revision, payload));
    }
}