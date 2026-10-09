using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using WinManager.Contracts;

namespace WinManager.Client;

public sealed class WinManagerClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim subscriptionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ResponseEnvelope>> pending = new();
    private readonly object sync = new();
    private readonly int eventQueueCapacity;
    private readonly Task reader;
    private ManagerSubscription? subscription;
    private Exception? failure;
    private int disposed;

    private WinManagerClient(NamedPipeClientStream pipe, int eventQueueCapacity)
    {
        this.pipe = pipe;
        this.eventQueueCapacity = eventQueueCapacity;
        lifetimeToken = lifetime.Token;
        reader = ReadAsync();
    }

    public bool IsConnected => Volatile.Read(ref failure) is null && pipe.IsConnected;

    public static async Task<WinManagerClient> ConnectAsync(string? pipeName = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, int eventQueueCapacity = 256)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WinManager IPC requires Windows.");
        if (eventQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(eventQueueCapacity));
        var connectTimeout = timeout ?? TimeSpan.FromSeconds(5);
        if (connectTimeout <= TimeSpan.Zero && connectTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var name = pipeName ?? Protocol.DefaultPipeName();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(connectTimeout);
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            return new WinManagerClient(pipe, eventQueueCapacity);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new TimeoutException($"Timed out connecting to pipe '{name}' after {connectTimeout}.", error);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async Task<ManagerSnapshot> QuerySnapshotAsync(CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync("query.snapshot", Protocol.Payload(new { }), cancellationToken);
        var snapshot = ReadPayload<ManagerSnapshot>(response);
        if (snapshot.InstanceId == Guid.Empty || snapshot.StateRevision != response.StateRevision ||
            snapshot.Desktop is null || snapshot.Trees is null || snapshot.Desktop.Monitors is null || snapshot.Desktop.Windows is null)
        {
            var error = new WinManagerProtocolException("Snapshot response is incomplete or has a mismatched state revision.");
            Abort(error);
            throw error;
        }
        return snapshot;
    }

    public async Task<CommandResult> ExecuteAsync(ManagerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var response = await RequestAsync("command", Protocol.Payload(command), cancellationToken);
        var result = ReadPayload<CommandResult>(response);
        if (result.StateRevision != response.StateRevision)
        {
            var error = new WinManagerProtocolException("Command result has a mismatched state revision.");
            Abort(error);
            throw error;
        }
        return result;
    }

    public async Task<ManagerSubscription> SubscribeAsync(IReadOnlyList<string>? eventTypes = null,
        CancellationToken cancellationToken = default)
    {
        await subscriptionGate.WaitAsync(cancellationToken);
        ManagerSubscription? created = null;
        try
        {
            lock (sync)
            {
                ThrowIfDisconnected();
                if (subscription is not null) throw new InvalidOperationException("Only one subscription per connection is supported.");
                created = new ManagerSubscription(this, eventQueueCapacity);
                subscription = created;
            }
            var response = await RequestAsync("subscribe", Protocol.Payload(new SubscribeRequest(eventTypes)), cancellationToken);
            var result = ReadPayload<SubscriptionResult>(response);
            if (!result.Success)
                throw new WinManagerRemoteException(result.ErrorCode ?? "subscription_failed", "Subscription was rejected.", result.StateRevision);
            if (result.SubscriptionId is not { } id || id == Guid.Empty)
                throw new WinManagerProtocolException("Subscription response has no subscription ID.");
            created.SubscriptionId = id;
            await created.InitialSnapshot.WaitAsync(cancellationToken);
            return created;
        }
        catch (Exception error)
        {
            if (created is not null) Abort(error);
            throw;
        }
        finally { subscriptionGate.Release(); }
    }

    public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        ManagerSubscription? active;
        lock (sync) active = subscription;
        if (active is not null) await UnsubscribeAsync(active, cancellationToken);
    }

    internal async Task UnsubscribeAsync(ManagerSubscription active, CancellationToken cancellationToken)
    {
        await subscriptionGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                if (subscription != active) return;
                if (failure is not null)
                {
                    subscription = null;
                    return;
                }
            }
            try
            {
                await RequestAsync("unsubscribe", Protocol.Payload(new { subscriptionId = active.SubscriptionId }), cancellationToken);
                lock (sync)
                {
                    subscription = null;
                    active.Complete();
                }
            }
            catch (Exception error) { Abort(error); throw; }
        }
        finally { subscriptionGate.Release(); }
    }

    private async Task<ResponseEnvelope> RequestAsync(string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new RequestEnvelope(Protocol.Version, Guid.NewGuid(), operation, payload);
        var completion = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ThrowIfDisconnected();
            pending[request.RequestId] = completion;
        }
        using var cancellation = cancellationToken.Register(() => Abort(new OperationCanceledException(cancellationToken)));
        try
        {
            await writeGate.WaitAsync(lifetimeToken);
            try { await PipeFraming.WriteAsync(pipe, request, lifetimeToken); }
            finally { writeGate.Release(); }
        }
        catch (Exception error) { Abort(error); }
        var response = await completion.Task;
        if (!response.Success)
            throw new WinManagerRemoteException(response.ErrorCode ?? "server_error", response.Message ?? "Request failed.", response.StateRevision);
        return response;
    }

    private async Task ReadAsync()
    {
        try
        {
            while (true)
            {
                using var document = await PipeFraming.ReadAsync(pipe, lifetimeToken)
                    ?? throw new IOException("The WinManager server disconnected.");
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new WinManagerProtocolException("Server envelope must be an object.");
                if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var number) || number != Protocol.Version)
                    throw new WinManagerProtocolException("Unsupported or missing protocol version.");
                if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    throw new WinManagerProtocolException("Missing envelope kind.");
                if (!root.TryGetProperty("stateRevision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
                    !revision.TryGetInt64(out var stateRevision) || stateRevision < 0)
                    throw new WinManagerProtocolException("Missing or invalid state revision.");
                switch (kind.GetString())
                {
                    case "response":
                        if (!root.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new WinManagerProtocolException("Response has no success flag.");
                        var response = Deserialize<ResponseEnvelope>(root);
                        if (response.RequestId == Guid.Empty || !pending.TryRemove(response.RequestId, out var completion))
                            throw new WinManagerProtocolException("Response has an unknown request ID.");
                        completion.TrySetResult(response);
                        break;
                    case "event":
                        var envelope = Deserialize<EventEnvelope>(root);
                        if (string.IsNullOrWhiteSpace(envelope.EventType))
                            throw new WinManagerProtocolException("Missing event type.");
                        lock (sync)
                        {
                            if (subscription is null) throw new WinManagerProtocolException("Received an event without an active subscription.");
                            if (!subscription.TryPublish(envelope)) throw new WinManagerConsumerLagException(eventQueueCapacity);
                        }
                        break;
                    default: throw new WinManagerProtocolException("Unknown server envelope kind.");
                }
            }
        }
        catch (Exception error)
        {
            Abort(error is JsonException or InvalidDataException or EndOfStreamException or InvalidOperationException
                ? new WinManagerProtocolException("Invalid server frame.", error) : error);
        }
    }

    internal static T Deserialize<T>(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new WinManagerProtocolException($"{typeof(T).Name} payload must be an object.");
        if (typeof(T) == typeof(CommandResult) || typeof(T) == typeof(SubscriptionResult))
        {
            if (!payload.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new WinManagerProtocolException($"{typeof(T).Name} has no success flag.");
        }
        if (typeof(T) == typeof(ManagerSnapshot))
        {
            if (!payload.TryGetProperty("paused", out var paused) || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !payload.TryGetProperty("stateRevision", out var revision) || revision.ValueKind != JsonValueKind.Number)
                throw new WinManagerProtocolException("Snapshot is incomplete.");
        }
        return payload.Deserialize<T>(Protocol.JsonOptions)
            ?? throw new WinManagerProtocolException($"Missing {typeof(T).Name} payload.");
    }

    private T ReadPayload<T>(ResponseEnvelope response)
    {
        try
        {
            var payload = response.Payload ?? throw new WinManagerProtocolException("Response has no payload.");
            return Deserialize<T>(payload);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or WinManagerProtocolException)
        {
            var protocolError = error as WinManagerProtocolException
                ?? new WinManagerProtocolException($"Invalid {typeof(T).Name} response payload.", error);
            Abort(protocolError);
            throw protocolError;
        }
    }

    private void ThrowIfDisconnected()
    {
        if (failure is { } error) throw error;
    }

    private void Abort(Exception error)
    {
        lock (sync)
        {
            if (failure is not null) return;
            Volatile.Write(ref failure, error);
            lifetime.Cancel();
            pipe.Dispose();
            foreach (var entry in pending)
                if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(error);
            subscription?.Complete(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Abort(new ObjectDisposedException(nameof(WinManagerClient)));
        await reader;
        if (Interlocked.Exchange(ref disposed, 1) == 0) lifetime.Dispose();
    }

    public static async IAsyncEnumerable<EventEnvelope> WatchAsync(IReadOnlyList<string>? eventTypes = null,
        string? pipeName = null, bool reconnect = false, TimeSpan? connectTimeout = null,
        TimeSpan? reconnectDelay = null, int eventQueueCapacity = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var delay = reconnectDelay ?? TimeSpan.FromSeconds(1);
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WinManagerClient? client = null;
            ManagerSubscription? active = null;
            try
            {
                try
                {
                    client = await ConnectAsync(pipeName, connectTimeout, cancellationToken, eventQueueCapacity);
                    active = await client.SubscribeAsync(eventTypes, cancellationToken);
                }
                catch (Exception error) when (reconnect && IsReconnectable(error) && !cancellationToken.IsCancellationRequested) { }
                if (active is not null)
                {
                    await using var enumerator = active.ReadEventsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                    while (true)
                    {
                        bool moved;
                        try { moved = await enumerator.MoveNextAsync(); }
                        catch (Exception error) when (reconnect && IsReconnectable(error) && !cancellationToken.IsCancellationRequested) { break; }
                        if (!moved) break;
                        yield return enumerator.Current;
                    }
                }
            }
            finally
            {
                if (client is not null) await client.DisposeAsync();
            }
            if (!reconnect) yield break;
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsReconnectable(Exception error) => error is IOException or TimeoutException;
}