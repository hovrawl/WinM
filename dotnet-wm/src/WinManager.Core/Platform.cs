using WinManager.Contracts;

namespace WinManager.Core;

public enum NativeNotificationKind { WindowChanged, FocusChanged, DisplayChanged, Reconcile, MoveStarted, MoveEnded }
public sealed record NativeNotification(NativeNotificationKind Kind, Guid? WindowId = null, PixelRect? Bounds = null);
public interface IDesktopObserver : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<NativeNotification> Notifications(CancellationToken cancellationToken);
    Task<DesktopSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class DesktopObservation(IDesktopObserver platform, TimeSpan? rescanInterval = null)
{
    public DesktopSnapshot? Current { get; private set; }
    public event Action<DesktopSnapshot>? Changed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await platform.StartAsync(cancellationToken);
        await ReconcileAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var periodic = RescanAsync(linked.Token);
        var notifications = ObserveAsync(linked.Token);
        try
        {
            await await Task.WhenAny(periodic, notifications);
        }
        finally
        {
            await linked.CancelAsync();
            try { await Task.WhenAll(periodic, notifications); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        await foreach (var _ in platform.Notifications(cancellationToken))
            await ReconcileAsync(cancellationToken);
    }

    private readonly SemaphoreSlim gate = new(1);
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = await platform.CaptureAsync(cancellationToken);
            if (Current is null || Current.FocusedWindowId != next.FocusedWindowId ||
                !Current.Windows.SequenceEqual(next.Windows) || !Current.Monitors.SequenceEqual(next.Monitors))
            {
                Current = next;
                Changed?.Invoke(next);
            }
        }
        finally { gate.Release(); }
    }

    private async Task RescanAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(rescanInterval ?? TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await ReconcileAsync(cancellationToken);
    }
}