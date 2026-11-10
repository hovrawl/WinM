using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinManager.Contracts;

namespace WinManager.Client;

public sealed class ManagerSubscription : IAsyncDisposable
{
    private readonly WinManagerClient client;
    private readonly Channel<EventEnvelope> events;
    private readonly TaskCompletionSource initialSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int enumerated;
    private Guid? instanceId;
    private long lastSequence;
    private long lastRevision;

    internal ManagerSubscription(WinManagerClient client, int capacity)
    {
        this.client = client;
        events = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    public Guid SubscriptionId { get; internal set; }
    public Guid InstanceId => instanceId ?? Guid.Empty;
    internal Task InitialSnapshot => initialSnapshot.Task;

    internal bool TryPublish(EventEnvelope envelope)
    {
        var snapshot = WinManagerClient.Deserialize<ManagerSnapshot>(envelope.Payload);
        if (envelope.InstanceId == Guid.Empty || snapshot.InstanceId != envelope.InstanceId ||
            envelope.Sequence < 0 || envelope.StateRevision < 0 ||
            snapshot.StateRevision != envelope.StateRevision || snapshot.Desktop is null || snapshot.Trees is null ||
            snapshot.Desktop.Monitors is null || snapshot.Desktop.Windows is null)
            throw new WinManagerProtocolException("Event payload must be a full snapshot matching its envelope.");
        if (instanceId is null)
        {
            if (envelope.EventType != ManagerEventTypes.Snapshot)
                throw new WinManagerProtocolException("The first subscription event must be a snapshot.");
        }
        else if (envelope.InstanceId != instanceId || envelope.Sequence <= lastSequence || envelope.StateRevision < lastRevision)
            throw new WinManagerProtocolException("Subscription instance, sequence, or state revision changed out of order.");
        if (!events.Writer.TryWrite(envelope)) return false;
        instanceId = envelope.InstanceId;
        lastSequence = envelope.Sequence;
        lastRevision = envelope.StateRevision;
        initialSnapshot.TrySetResult();
        return true;
    }

    internal void Complete(Exception? error = null)
    {
        events.Writer.TryComplete(error);
        if (error is not null) initialSnapshot.TrySetException(error);
        else initialSnapshot.TrySetCanceled();
    }

    public async IAsyncEnumerable<EventEnvelope> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
            throw new InvalidOperationException("A subscription supports one event consumer only.");
        await foreach (var envelope in events.Reader.ReadAllAsync(cancellationToken))
            yield return envelope;
    }

    public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => client.UnsubscribeAsync(this, cancellationToken);
    public ValueTask DisposeAsync() => new(UnsubscribeAsync());
}