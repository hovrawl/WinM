namespace WinManager.SpatialSwitcher;

internal static class ConnectionRetry
{
    public static async Task RunAsync(Func<CancellationToken, Task> connect, Func<bool> isConnected,
        TimeSpan interval, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isConnected()) await connect(cancellationToken);
            await delay(interval, cancellationToken);
        }
    }
}
