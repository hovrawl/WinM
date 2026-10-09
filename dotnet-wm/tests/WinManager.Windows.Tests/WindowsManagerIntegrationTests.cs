using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;
using Xunit.Abstractions;

namespace WinManager.Windows.Tests;

[Collection("Isolated interactive desktop")]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowsManagerIntegrationTests(ITestOutputHelper output)
{
    private const int InnerGap = 12;
    private const int OuterGap = 16;

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task EngineTilesTwoNativeProcessesHorizontallyThenVerticallyResizesAndSwaps()
    {
        await using var first = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var second = await NativeWindowFixture.StartAsync(layoutOnly: true);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
        await using var platform = new TestOwnedPlatform(first, second);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await platform.StartAsync(timeout.Token);
        var initial = await platform.CaptureAsync(timeout.Token);
        Assert.Equal(2, initial.Windows.Count);
        Assert.All(initial.Windows, window => Assert.True(window.Visible && !window.Minimized && !window.Fullscreen));
        var monitor = Assert.Single(initial.Monitors, monitor => monitor.Id == initial.Windows[0].MonitorId);
        Assert.All(initial.Windows, window => Assert.Equal(monitor.Id, window.MonitorId));
        var tree = new SplitTree();
        tree.Reconcile(initial.Windows);
        var engine = new ManagerEngine(platform, InnerGap, OuterGap);
        var run = engine.RunAsync(timeout.Token);
        try
        {
            var horizontal = tree.Layout(monitor, InnerGap, OuterGap);
            var observed = await WaitForLayoutAsync(engine, run, platform, horizontal, timeout.Token);
            AssertPartition(observed, monitor, SplitOrientation.Horizontal);
            Assert.True(platform.PlacementCount >= 2);
            await HoldAsync("horizontal", observed, timeout.Token);

            var target = horizontal.Placements[0].WindowId;
            await ExecuteAsync(engine, new(CommandKind.SetSplit, target, Orientation: SplitOrientation.Vertical), timeout.Token);
            tree.SetOrientation(target, SplitOrientation.Vertical);
            var vertical = tree.Layout(monitor, InnerGap, OuterGap);
            observed = await WaitForLayoutAsync(engine, run, platform, vertical, timeout.Token);
            AssertPartition(observed, monitor, SplitOrientation.Vertical);
            Assert.NotEqual(horizontal.Placements[0].Bounds, vertical.Placements[0].Bounds);
            await HoldAsync("vertical", observed, timeout.Token);

            await ExecuteAsync(engine, new(CommandKind.Resize, target, Direction.Down, .1), timeout.Token);
            Assert.True(tree.Resize(target, Direction.Down, .1));
            var resized = tree.Layout(monitor, InnerGap, OuterGap);
            observed = await WaitForLayoutAsync(engine, run, platform, resized, timeout.Token);
            AssertPartition(observed, monitor, SplitOrientation.Vertical);
            Assert.NotEqual(vertical.Placements[0].Bounds.Height, resized.Placements[0].Bounds.Height);
            await HoldAsync("resized vertical", observed, timeout.Token);

            var neighbor = tree.Neighbor(target, Direction.Down, observed.Windows);
            Assert.NotNull(neighbor);
            await ExecuteAsync(engine, new(CommandKind.Move, target, Direction.Down), timeout.Token);
            Assert.True(tree.Swap(target, neighbor.Value));
            var swapped = tree.Layout(monitor, InnerGap, OuterGap);
            observed = await WaitForLayoutAsync(engine, run, platform, swapped, timeout.Token);
            Assert.Equal(resized.Placements.Single(placement => placement.WindowId == neighbor).Bounds,
                observed.Windows.Single(window => window.Id == target).Bounds);
            AssertPartition(observed, monitor, SplitOrientation.Vertical);
            Assert.True(platform.NotificationCount > 0);
            Assert.Empty(platform.Failures);
            await HoldAsync("swapped vertical", observed, timeout.Token);

            var invalid = await engine.ExecuteAsync(new(CommandKind.Resize, target, Direction.Down, double.NaN), timeout.Token);
            Assert.False(invalid.Success);
            Assert.Equal("invalid_argument", invalid.ErrorCode);
            await WaitForLayoutAsync(engine, run, platform, swapped, timeout.Token);
        }
        finally { await StopAsync(engine, run, timeout); }
        Assert.Empty(engine.ShutdownFailures);
        Assert.True(platform.RestoreCount > 0);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task EngineReportsRealNativeMinimumConstraintFailureWithoutMovingEitherWindow()
    {
        await using var first = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var second = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await first.CommandAsync("impossible-minimum");
        await second.CommandAsync("impossible-minimum");
        await using var platform = new TestOwnedPlatform(first, second);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await platform.StartAsync(timeout.Token);
        var initial = await platform.CaptureAsync(timeout.Token);
        var constrained = Assert.Single(initial.Windows, window => window.Title == first.Prefix + ".normal");
        var monitor = Assert.Single(initial.Monitors, monitor => monitor.Id == constrained.MonitorId);
        Assert.Equal(2, initial.Windows.Count);
        Assert.All(initial.Windows, window =>
        {
            Assert.Equal(monitor.Id, window.MonitorId);
            Assert.True(window.MinimumWidth > monitor.WorkArea.Width);
            Assert.True(window.MinimumHeight > monitor.WorkArea.Height);
        });
        var engine = new ManagerEngine(platform, InnerGap, OuterGap);
        var diagnostic = new ConcurrentQueue<NativeActionFailure>();
        engine.ActionsFailed += failures =>
        {
            foreach (var failure in failures) diagnostic.Enqueue(failure);
        };
        var tree = new SplitTree();
        tree.Reconcile(initial.Windows);
        var expected = tree.Layout(monitor, InnerGap, OuterGap);
        Assert.Equal(initial.Windows.Count, expected.Placements.Count);
        Assert.All(initial.Windows, window =>
        {
            var placement = Assert.Single(expected.Placements, placement => placement.WindowId == window.Id);
            Assert.True(placement.Bounds.Width < window.MinimumWidth);
            Assert.True(placement.Bounds.Height < window.MinimumHeight);
        });
        var run = engine.RunAsync(timeout.Token);
        try
        {
            await WaitForCurrentAsync(engine, run, timeout.Token);
            var result = await engine.ExecuteAsync(new(CommandKind.Redraw), timeout.Token);
            Assert.True(result.Success);
            var actual = await WaitForLayoutAsync(engine, run, platform, expected, timeout.Token);
            Assert.True(platform.PlacementCount >= initial.Windows.Count);
            Assert.Equal(2, actual.Windows.Count);
            AssertPartition(actual, monitor, SplitOrientation.Horizontal);
            Assert.All(actual.Windows, window => Assert.True(window.Visible && !window.ManagerHidden));
            Assert.DoesNotContain(diagnostic, failure => failure.Code == "layout_overflow");
            Assert.Empty(diagnostic);
            Assert.Empty(platform.Failures);
        }
        finally { await StopAsync(engine, run, timeout); }
        Assert.DoesNotContain(diagnostic, failure => failure.Code == "layout_overflow");
        Assert.Empty(diagnostic);
        Assert.Empty(engine.ShutdownFailures);
    }

    private static async Task ExecuteAsync(ManagerEngine engine, ManagerCommand command, CancellationToken token)
    {
        var result = await engine.ExecuteAsync(command, token);
        Assert.True(result.Success, $"{command.Kind}: {result.ErrorCode}: {result.Message}");
    }

    private static async Task WaitForCurrentAsync(ManagerEngine engine, Task run, CancellationToken token)
    {
        while (engine.Current is null)
        {
            Assert.False(run.IsCompleted, "Manager stopped before its first capture.");
            await Task.Delay(20, token);
        }
    }

    private async Task<DesktopSnapshot> WaitForLayoutAsync(ManagerEngine engine, Task run, TestOwnedPlatform platform,
        LayoutPlan expected, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        DesktopSnapshot? actual = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (run.IsCompleted) await run;
            Assert.False(run.IsCompleted, "Manager stopped before native layout convergence.");
            Assert.Empty(platform.Failures);
            actual = await platform.CaptureAsync(token);
            if (Matches(actual, expected) && engine.Current is { } current && Matches(current.Desktop, expected))
            {
                Assert.All(actual.Windows, window => Assert.True(window.Visible && !window.ManagerHidden));
                return actual;
            }
            await Task.Delay(30, token);
        }
        Assert.Fail($"Native layout did not converge. Expected: {string.Join("; ", expected.Placements)}. " +
            $"Observed: {string.Join("; ", actual?.Windows.Select(window => $"{window.Id}: {window.Bounds}") ?? [])}");
        throw new InvalidOperationException();
    }

    private static bool Matches(DesktopSnapshot actual, LayoutPlan expected) =>
        actual.Windows.Count == expected.Placements.Count && expected.Placements.All(placement =>
            actual.Windows.Any(window => window.Id == placement.WindowId && window.Bounds == placement.Bounds));

    private static void AssertPartition(DesktopSnapshot desktop, MonitorInfo monitor, SplitOrientation orientation)
    {
        var inner = (int)Math.Round(InnerGap * monitor.Dpi / 96.0, MidpointRounding.AwayFromZero);
        var outer = (int)Math.Round(OuterGap * monitor.Dpi / 96.0, MidpointRounding.AwayFromZero);
        var work = monitor.WorkArea;
        var bounds = desktop.Windows.Select(window => window.Bounds).OrderBy(bounds =>
            orientation == SplitOrientation.Horizontal ? bounds.X : bounds.Y).ToArray();
        Assert.Equal(2, bounds.Length);
        Assert.Equal(work.X + outer, bounds[0].X);
        Assert.Equal(work.Y + outer, bounds[0].Y);
        if (orientation == SplitOrientation.Horizontal)
        {
            Assert.Equal(bounds[0].X + bounds[0].Width + inner, bounds[1].X);
            Assert.Equal(bounds[0].Y, bounds[1].Y);
            Assert.Equal(work.Height - 2 * outer, bounds[0].Height);
            Assert.Equal(bounds[0].Height, bounds[1].Height);
            Assert.Equal(work.X + work.Width - outer, bounds[1].X + bounds[1].Width);
        }
        else
        {
            Assert.Equal(bounds[0].Y + bounds[0].Height + inner, bounds[1].Y);
            Assert.Equal(bounds[0].X, bounds[1].X);
            Assert.Equal(work.Width - 2 * outer, bounds[0].Width);
            Assert.Equal(bounds[0].Width, bounds[1].Width);
            Assert.Equal(work.Y + work.Height - outer, bounds[1].Y + bounds[1].Height);
        }
    }

    private async Task HoldAsync(string phase, DesktopSnapshot desktop, CancellationToken token)
    {
        output.WriteLine($"{phase}: {string.Join("; ", desktop.Windows.Select(window => $"{window.Title}: {window.Bounds}"))}");
        if (double.TryParse(Environment.GetEnvironmentVariable("WINMANAGER_TEST_LAYOUT_HOLD_SECONDS"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 15) / 4), token);
    }

    private static async Task StopAsync(ManagerEngine engine, Task run, CancellationTokenSource timeout)
    {
        try
        {
            if (!run.IsCompleted && !timeout.IsCancellationRequested)
                await ExecuteAsync(engine, new(CommandKind.Exit), timeout.Token);
        }
        finally
        {
            await timeout.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private sealed class TestOwnedPlatform(params NativeWindowFixture[] fixtures) : IWindowPlatform
    {
        private readonly WindowsDesktopObserver observer = new(recoveryExecutablePath:
            Path.Combine(AppContext.BaseDirectory, "Recovery", "WinManager.Recovery.exe"));
        private readonly ConcurrentDictionary<Guid, byte> allowed = new();
        private readonly ConcurrentQueue<NativeActionFailure> failures = new();
        private int placementCount;
        private int notificationCount;
        private int restoreCount;
        public int PlacementCount => Volatile.Read(ref placementCount);
        public int NotificationCount => Volatile.Read(ref notificationCount);
        public int RestoreCount => Volatile.Read(ref restoreCount);
        public IReadOnlyList<NativeActionFailure> Failures => failures.ToArray();

        public Task StartAsync(CancellationToken token) => observer.StartAsync(token);

        public async IAsyncEnumerable<NativeNotification> Notifications([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var notification in observer.Notifications(token))
            {
                Interlocked.Increment(ref notificationCount);
                yield return notification;
            }
        }

        public async Task<DesktopSnapshot> CaptureAsync(CancellationToken token)
        {
            var desktop = await observer.CaptureAsync(token);
            var windows = desktop.Windows.Where(window => fixtures.Any(fixture =>
                window.Title == fixture.Prefix + ".normal" && window.ClassName == fixture.WindowClass)).ToArray();
            foreach (var window in windows)
            {
                var fixture = fixtures.Single(fixture => window.Title == fixture.Prefix + ".normal");
                Assert.Equal((uint)fixture.ProcessId, NativeInspection.ProcessId(window.Title));
                allowed.TryAdd(window.Id, 0);
            }
            return desktop with
            {
                Windows = windows,
                FocusedWindowId = windows.Any(window => window.Id == desktop.FocusedWindowId) ? desktop.FocusedWindowId : null
            };
        }

        public async Task<ApplyResult> ApplyAsync(LayoutPlan plan, CancellationToken token)
        {
            var ids = plan.Placements.Select(placement => placement.WindowId)
                .Concat(plan.Visibility?.Select(visibility => visibility.WindowId) ?? []);
            if (plan.FocusWindowId is { } focus) ids = ids.Append(focus);
            Assert.All(ids, id => Assert.True(allowed.ContainsKey(id), "Refusing action outside disposable test-owned windows."));
            Interlocked.Add(ref placementCount, plan.Placements.Count);
            var result = await observer.ApplyAsync(plan, token);
            foreach (var failure in result.Failures) failures.Enqueue(failure);
            return result;
        }

        public async Task<ApplyResult> RestoreAsync(CancellationToken token)
        {
            Interlocked.Increment(ref restoreCount);
            var result = await observer.RestoreAsync(token);
            foreach (var failure in result.Failures) failures.Enqueue(failure);
            return result;
        }

        public ValueTask DisposeAsync() => observer.DisposeAsync();
    }
}