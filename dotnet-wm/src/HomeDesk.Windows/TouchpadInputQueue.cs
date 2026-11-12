using System.Threading.Channels;

namespace HomeDesk.Windows;

internal sealed class TouchpadInputQueue<T>(int capacity)
{
    private readonly Channel<TouchpadInputQueueItem<T>> channel =
        Channel.CreateBounded<TouchpadInputQueueItem<T>>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    private int inputLost;

    public bool TryPublish(T value)
    {
        var resetBefore = Interlocked.Exchange(ref inputLost, 0) != 0;
        if (channel.Writer.TryWrite(new TouchpadInputQueueItem<T>(value, resetBefore))) return true;
        Interlocked.Exchange(ref inputLost, 1);
        return false;
    }

    public bool TryRead(out TouchpadInputQueueItem<T> value) => channel.Reader.TryRead(out value);

    public void MarkInputLost() => Interlocked.Exchange(ref inputLost, 1);

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        channel.Reader.WaitToReadAsync(cancellationToken);

    public IAsyncEnumerable<TouchpadInputQueueItem<T>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete(Exception? error = null) => channel.Writer.TryComplete(error);
}

internal readonly record struct TouchpadInputQueueItem<T>(T Value, bool ResetBefore);