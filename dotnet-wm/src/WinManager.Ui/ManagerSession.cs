using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;

namespace WinManager.Ui;

public sealed class ManagerSession : IAsyncDisposable
{
    private readonly WinManagerClient client;
    private readonly ManagerSubscription subscription;
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? runCancellation;
    private Task? pump;
    private int started;
    private int disposed;

    private ManagerSession(WinManagerClient client, ManagerSubscription subscription)
    {
        this.client = client;
        this.subscription = subscription;
    }

    public bool IsConnected => client.IsConnected;

    public static async Task<ManagerSession> ConnectAsync(string? pipeName = null,
        CancellationToken cancellationToken = default, int eventQueueCapacity = 64)
    {
        var client = await WinManagerClient.ConnectAsync(pipeName, timeout: TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken, eventQueueCapacity: eventQueueCapacity);
        try
        {
            var subscription = await client.SubscribeAsync(cancellationToken: cancellationToken);
            return new ManagerSession(client, subscription);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public void Start(Action<ManagerSnapshot, string> snapshotReceived, Action<Exception?> disconnected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotReceived);
        ArgumentNullException.ThrowIfNull(disconnected);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("Manager session is already running.");
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellationToken);
        pump = PumpAsync(snapshotReceived, disconnected, runCancellation);
    }

    public Task<CommandResult> ExecuteAsync(ManagerCommand command, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync(command, cancellationToken);

    private async Task PumpAsync(Action<ManagerSnapshot, string> snapshotReceived,
        Action<Exception?> disconnected, CancellationTokenSource cancellation)
    {
        Exception? failure = null;
        try
        {
            await foreach (var envelope in subscription.ReadEventsAsync(cancellation.Token))
            {
                var snapshot = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)
                               ?? throw new InvalidDataException("Manager event did not contain a snapshot.");
                snapshotReceived(snapshot, envelope.EventType);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            cancellation.Dispose();
            if (!shutdown.IsCancellationRequested)
            {
                await client.DisposeAsync();
                disconnected(failure);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await shutdown.CancelAsync();
        await client.DisposeAsync();
        if (pump is not null) await pump;
        shutdown.Dispose();
    }
}