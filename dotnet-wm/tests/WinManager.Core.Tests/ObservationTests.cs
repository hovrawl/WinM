using System.Threading.Channels;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

public sealed class ObservationTests
{
    private static DesktopSnapshot Snapshot(string title = "Window", Guid? windowId = null) =>
        new([], [new(windowId ?? WindowId, MonitorId, title, "test", "test",
            new(-1920, 0, 800, 600), true, false, false, WindowMode.Tiling)], windowId ?? WindowId);
    private static readonly Guid WindowId = Guid.NewGuid();
    private static readonly Guid MonitorId = Guid.NewGuid();

    [Fact]
    public async Task HooksStartBeforeInitialCaptureAndBufferedChangesAreReconciled()
    {
        await using var fake = new FakeObserver(Snapshot());
        fake.OnStart = () => fake.Notify();
        var observation = new DesktopObservation(fake);
        using var stop = new CancellationTokenSource();
        var run = observation.RunAsync(stop.Token);
        await fake.WaitForCapturesAsync(2);
        Assert.Equal(new[] { "start", "capture", "capture" }, fake.Calls.Take(3));
        Assert.Equal(Snapshot().Windows, observation.Current!.Windows);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task DuplicateAndOutOfOrderNotificationsPublishOnlyObservedChanges()
    {
        await using var fake = new FakeObserver(Snapshot());
        var observation = new DesktopObservation(fake);
        var published = new List<DesktopSnapshot>();
        observation.Changed += published.Add;
        await observation.ReconcileAsync(default);
        await observation.ReconcileAsync(default);
        fake.Snapshot = Snapshot("Latest");
        await observation.ReconcileAsync(default);
        await observation.ReconcileAsync(default);
        Assert.Equal(2, published.Count);
        Assert.Equal("Latest", published[1].Windows.Single().Title);
    }

    [Fact]
    public async Task IdentityReplacementAndRemovalAreNotSuppressed()
    {
        await using var fake = new FakeObserver(Snapshot());
        var observation = new DesktopObservation(fake);
        var published = new List<DesktopSnapshot>();
        observation.Changed += published.Add;
        await observation.ReconcileAsync(default);
        var replacement = Guid.NewGuid();
        fake.Snapshot = Snapshot(windowId: replacement);
        await observation.ReconcileAsync(default);
        fake.Snapshot = new([], [], null);
        await observation.ReconcileAsync(default);
        Assert.Equal(3, published.Count);
        Assert.Equal(replacement, published[1].Windows.Single().Id);
        Assert.Empty(observation.Current!.Windows);
    }

    [Fact]
    public async Task MonitorAndFocusChangesPublishEvenWithoutWindowChanges()
    {
        await using var fake = new FakeObserver(new([], [], null));
        var observation = new DesktopObservation(fake);
        var count = 0;
        observation.Changed += _ => count++;
        await observation.ReconcileAsync(default);
        fake.Snapshot = new([new(MonitorId, "display", new(-1000, 0, 1000, 900),
            new(-1000, 0, 1000, 850), 144)], [], WindowId);
        await observation.ReconcileAsync(default);
        Assert.Equal(2, count);
        Assert.Equal(144u, observation.Current!.Monitors.Single().Dpi);
    }

    [Fact]
    public async Task CaptureFailureLeavesLastCommittedSnapshotIntact()
    {
        await using var fake = new FakeObserver(Snapshot());
        var observation = new DesktopObservation(fake);
        await observation.ReconcileAsync(default);
        fake.FailCapture = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => observation.ReconcileAsync(default));
        Assert.Equal(Snapshot().Windows, observation.Current!.Windows);
        fake.FailCapture = false;
        await observation.ReconcileAsync(default);
    }

    [Fact]
    public async Task PeriodicRescanClosesMissedNotificationRace()
    {
        await using var fake = new FakeObserver(Snapshot());
        var observation = new DesktopObservation(fake, TimeSpan.FromMilliseconds(20));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observation.Changed += snapshot =>
        {
            if (snapshot.Windows.Single().Title == "Missed") changed.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = observation.RunAsync(stop.Token);
        fake.Snapshot = Snapshot("Missed");
        await changed.Task.WaitAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task PeriodicCaptureFailureTerminatesObservationInsteadOfFailingSilently()
    {
        await using var fake = new FakeObserver(Snapshot());
        var observation = new DesktopObservation(fake, TimeSpan.FromMilliseconds(20));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = observation.RunAsync(stop.Token);
        fake.FailCapture = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }

    private sealed class FakeObserver(DesktopSnapshot snapshot) : IDesktopObserver
    {
        public DesktopSnapshot Snapshot { get; set; } = snapshot;
        public List<string> Calls { get; } = [];
        public Action? OnStart { get; set; }
        public bool FailCapture { get; set; }
        private readonly Channel<NativeNotification> notifications = Channel.CreateUnbounded<NativeNotification>();
        private readonly Channel<int> captures = Channel.CreateUnbounded<int>();
        private int captureCount;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Calls.Add("start");
            OnStart?.Invoke();
            return Task.CompletedTask;
        }
        public void Notify() => notifications.Writer.TryWrite(new(NativeNotificationKind.WindowChanged));
        public IAsyncEnumerable<NativeNotification> Notifications(CancellationToken cancellationToken) =>
            notifications.Reader.ReadAllAsync(cancellationToken);
        public Task<DesktopSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("capture");
            if (FailCapture) throw new InvalidOperationException("Capture failed");
            captures.Writer.TryWrite(++captureCount);
            return Task.FromResult(Snapshot);
        }
        public async Task WaitForCapturesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await captures.Reader.ReadAsync(timeout.Token) < count) { }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}