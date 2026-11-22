using System.Runtime.CompilerServices;
using System.Threading.Channels;
using HomeDesk.Contracts;
using Xunit;

namespace HomeDesk.Core.Tests;

public sealed class ManagerTests
{
    [Fact]
    public async Task FailedGeometryAndFocusNeverBecomeCommittedPhysicalState()
    {
        await using var fake = new FakeWindowPlatform { FailActions = true };
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var original = fake.Desktop.Windows[0].Bounds;
        var result = await engine.ExecuteAsync(new(CommandKind.Focus, fake.Desktop.Windows[0].Id), stop.Token);
        Assert.False(result.Success);
        Assert.Equal("access_denied", result.ErrorCode);
        Assert.Equal(original, engine.Current!.Desktop.Windows[0].Bounds);
        Assert.Null(engine.Current.Desktop.FocusedWindowId);
        await Stop(engine, run);
    }

    [Fact]
    public async Task CommandsSerializeAndFloatingWindowsAreExcluded()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var id = fake.Desktop.Windows[0].Id;
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Floating), stop.Token))
            .Success);
        Assert.Equal(WindowMode.Floating, engine.Current!.Desktop.Windows.Single(window => window.Id == id).Mode);
        fake.Plans.Clear();
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token)).Success);
        Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Placements), placement => placement.WindowId == id);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Tiling), stop.Token))
            .Success);
        Assert.Equal(1, fake.MaximumConcurrentActions);
        await Stop(engine, run);
    }

    [Fact]
    public async Task PauseStopsGeometryActionsAndResumeRelayouts()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), stop.Token)).Success);
        fake.Desktop = fake.Desktop with
        {
            Windows = fake.Desktop.Windows.Select(window =>
                window with { Bounds = new(200, 200, 200, 200) }).ToArray()
        };
        fake.Plans.Clear();
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Empty(fake.Plans);
        Assert.True(engine.Current!.Paused);
        await engine.ExecuteAsync(new(CommandKind.Pause, Paused: false), stop.Token);
        Assert.NotEmpty(fake.Plans);
        Assert.False(engine.Current!.Paused);
        await Stop(engine, run);
    }

    [Fact]
    public async Task DuplicateNotificationsAndSelfInducedChangesDoNotLoopOrIncrementRevision()
    {
        await using var fake = new FakeWindowPlatform { NotifyOnApply = true };
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var revision = engine.Current!.StateRevision;
        for (var index = 0; index < 1000; index++) fake.Notify();
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.Equal(revision, engine.Current!.StateRevision);
        Assert.Single(fake.Plans);
        await Stop(engine, run);
    }

    [Fact]
    public async Task BoundedAdmissionReturnsBusyAndCanceledInputsDoNotExecute()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake, capacity: 1);
        using var cancellation = new CancellationTokenSource();
        var pending = engine.ExecuteAsync(new(CommandKind.Pause, Paused: true), cancellation.Token);
        var busy = await engine.ExecuteAsync(new(CommandKind.Redraw));
        Assert.Equal("busy", busy.ErrorCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token);
        Assert.False(engine.Current!.Paused);
        await Stop(engine, run);
    }

    [Fact]
    public async Task InvalidCommandsReturnStructuredErrorsWithoutMutation()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var revision = engine.Current!.StateRevision;
        var id = fake.Desktop.Windows[0].Id;
        var invalid = new ManagerCommand[]
        {
            new((CommandKind)100), new(CommandKind.Move, id),
            new(CommandKind.Resize, id, Direction.Right, double.NaN),
            new(CommandKind.SetMode, id, Mode: (WindowMode)100), new(CommandKind.Pause)
        };
        foreach (var command in invalid)
            Assert.Equal("invalid_argument", (await engine.ExecuteAsync(command, stop.Token)).ErrorCode);
        Assert.Equal(revision, engine.Current!.StateRevision);
        Assert.Equal("not_found",
            (await engine.ExecuteAsync(new(CommandKind.Focus, Guid.NewGuid()), stop.Token)).ErrorCode);
        await Stop(engine, run);
    }

    [Fact]
    public async Task CarouselMinimizeAndRestorePreserveMainFocusAndInsertAfterAnchor()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var target = fake.Desktop.Windows[0];
        var anchor = fake.Desktop.Windows[1];
        var workspace = engine.Current!.Workspaces!.Single(candidate => candidate.WindowIds.Contains(target.Id));
        var originalFocus = engine.Current.Desktop.FocusedWindowId;

        var minimized = await engine.ExecuteAsync(new(CommandKind.MinimizeWindow, target.Id), stop.Token);

        Assert.True(minimized.Success);
        Assert.True(engine.Current!.Desktop.Windows.Single(window => window.Id == target.Id).Minimized);
        Assert.DoesNotContain(target.Id, TreeWindowOrder(engine.Current.Trees[workspace.Id]));
        Assert.Equal(originalFocus, engine.Current.Desktop.FocusedWindowId);

        var restored = await engine.ExecuteAsync(new(CommandKind.RestoreWindow, target.Id,
            AfterWindowId: anchor.Id), stop.Token);

        Assert.True(restored.Success);
        Assert.False(engine.Current!.Desktop.Windows.Single(window => window.Id == target.Id).Minimized);
        Assert.Equal([anchor.Id, target.Id], TreeWindowOrder(engine.Current.Trees[workspace.Id]));
        Assert.Equal(originalFocus, engine.Current.Desktop.FocusedWindowId);
        await Stop(engine, run);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task CarouselRestoreInsertsAtSelectedIndexAndClampsToLayout(int restoreIndex)
    {
        await using var fake = new FakeWindowPlatform();
        var firstMonitor = fake.Desktop.Monitors[0];
        fake.Desktop = fake.Desktop with
        {
            Windows = [.. fake.Desktop.Windows,
                new(Guid.NewGuid(), firstMonitor.Id, "Window 2", "test", "test", new(-300, 50, 200, 200), true,
                    false, false, WindowMode.Tiling)]
        };
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var target = fake.Desktop.Windows[0];
        var workspace = engine.Current!.Workspaces!.Single(candidate => candidate.WindowIds.Contains(target.Id));
        var originalFocus = engine.Current.Desktop.FocusedWindowId;

        Assert.True((await engine.ExecuteAsync(new(CommandKind.MinimizeWindow, target.Id), stop.Token)).Success);
        var existingOrder = TreeWindowOrder(engine.Current!.Trees[workspace.Id]).ToList();
        var expectedOrder = existingOrder.ToList();
        expectedOrder.Insert(Math.Clamp(restoreIndex, 0, expectedOrder.Count), target.Id);

        var restored = await engine.ExecuteAsync(new(CommandKind.RestoreWindow, target.Id,
            RestoreIndex: restoreIndex), stop.Token);

        Assert.True(restored.Success);
        Assert.Equal(expectedOrder, TreeWindowOrder(engine.Current!.Trees[workspace.Id]));
        Assert.Equal(originalFocus, engine.Current.Desktop.FocusedWindowId);
        await Stop(engine, run);
    }

    [Fact]
    public async Task CarouselRestoreSupportsAnEmptyLayoutAndRejectsStaleAnchors()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var windows = fake.Desktop.Windows;
        var workspace = engine.Current!.Workspaces!.Single(candidate => candidate.WindowIds.Contains(windows[0].Id));

        foreach (var window in windows)
            Assert.True((await engine.ExecuteAsync(new(CommandKind.MinimizeWindow, window.Id), stop.Token)).Success);
        Assert.Empty(TreeWindowOrder(engine.Current!.Trees[workspace.Id]));

        var negativeIndex = await engine.ExecuteAsync(new(CommandKind.RestoreWindow, windows[0].Id,
            RestoreIndex: -1), stop.Token);
        Assert.False(negativeIndex.Success);
        Assert.Equal("invalid_argument", negativeIndex.ErrorCode);

        var conflictingPosition = await engine.ExecuteAsync(new(CommandKind.RestoreWindow, windows[0].Id,
            AfterWindowId: windows[1].Id, RestoreIndex: 0), stop.Token);
        Assert.False(conflictingPosition.Success);
        Assert.Equal("invalid_argument", conflictingPosition.ErrorCode);

        var invalidRestore = await engine.ExecuteAsync(new(CommandKind.RestoreWindow, windows[0].Id,
            AfterWindowId: Guid.NewGuid()), stop.Token);
        Assert.False(invalidRestore.Success);
        Assert.Equal("invalid_argument", invalidRestore.ErrorCode);
        Assert.True(engine.Current!.Desktop.Windows.Single(window => window.Id == windows[0].Id).Minimized);

        var restored = await engine.ExecuteAsync(new(CommandKind.RestoreWindow, windows[0].Id), stop.Token);
        Assert.True(restored.Success);
        Assert.Equal([windows[0].Id], TreeWindowOrder(engine.Current!.Trees[workspace.Id]));
        await Stop(engine, run);
    }

    [Fact]
    public async Task FailedCarouselMinimizeDoesNotLeaveAWindowInTheMinimizedRail()
    {
        await using var fake = new FakeWindowPlatform();
        var engine = new ManagerEngine(fake);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Start(engine, stop.Token, out var ready);
        await ready;
        var target = fake.Desktop.Windows[0];
        fake.FailActions = true;

        var result = await engine.ExecuteAsync(new(CommandKind.MinimizeWindow, target.Id), stop.Token);

        Assert.False(result.Success);
        Assert.Equal("access_denied", result.ErrorCode);
        Assert.False(engine.Current!.Desktop.Windows.Single(window => window.Id == target.Id).Minimized);
        fake.FailActions = false;
        await Stop(engine, run);
    }

    private static Task Start(ManagerEngine engine, CancellationToken cancellationToken, out Task ready)
    {
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += _ => initialized.TrySetResult();
        ready = initialized.Task.WaitAsync(cancellationToken);
        return engine.RunAsync(cancellationToken);
    }

    private static async Task Stop(ManagerEngine engine, Task run)
    {
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Exit))).Success);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Guid[] TreeWindowOrder(SplitNodeInfo? node) => node is null
        ? []
        : node.WindowId is { } windowId
            ? [windowId]
            : TreeWindowOrder(node.First).Concat(TreeWindowOrder(node.Second)).ToArray();
}

internal sealed class FakeWindowPlatform : IWindowPlatform
{
    private readonly Channel<NativeNotification> notifications = Channel.CreateUnbounded<NativeNotification>();
    private readonly HashSet<Guid> managerMinimized = [];
    private int activeActions;
    public DesktopSnapshot Desktop { get; set; }
    public bool FailActions { get; set; }
    public bool NotifyOnApply { get; set; }

    public TaskCompletionSource MoveNotificationRead { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int MaximumConcurrentActions { get; private set; }
    public List<LayoutPlan> Plans { get; } = [];

    public FakeWindowPlatform()
    {
        var monitorId = Guid.NewGuid();
        Desktop = new([new(monitorId, "display", new(-1000, 0, 1000, 900), new(-1000, 0, 1000, 850), 96)],
            Enumerable.Range(0, 2).Select(index => new WindowInfo(Guid.NewGuid(), monitorId, $"Window {index}",
                    "test", "test", new(-900 + index * 300, 50, 200, 200), true, false, false, WindowMode.Tiling))
                .ToArray(), null);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<DesktopSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(Desktop);

    public async IAsyncEnumerable<NativeNotification> Notifications(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken))
        {
            yield return notification;
            if (notification.Kind is NativeNotificationKind.MoveStarted or NativeNotificationKind.MoveEnded)
                MoveNotificationRead.TrySetResult();
        }
    }

    public void Notify() => notifications.Writer.TryWrite(new(NativeNotificationKind.Reconcile));

    public void NotifyMove(Guid windowId, bool started)
    {
        MoveNotificationRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.Writer.TryWrite(new(
            started ? NativeNotificationKind.MoveStarted : NativeNotificationKind.MoveEnded,
            windowId, started ? Desktop.Windows.FirstOrDefault(window => window.Id == windowId)?.Bounds : null));
    }

    public Task<ApplyResult> ApplyAsync(LayoutPlan plan, CancellationToken cancellationToken)
    {
        MaximumConcurrentActions = Math.Max(MaximumConcurrentActions, ++activeActions);
        Plans.Add(plan);
        if (!FailActions)
        {
            Desktop = Desktop with
            {
                Windows = Desktop.Windows.Select(window =>
                {
                    var placement = plan.Placements.FirstOrDefault(placement => placement.WindowId == window.Id);
                    var visibility = plan.Visibility?.FirstOrDefault(visibility => visibility.WindowId == window.Id);
                    var minimization =
                        plan.Minimizations?.FirstOrDefault(minimization => minimization.WindowId == window.Id);
                    if (minimization is { Minimized: true }) managerMinimized.Add(window.Id);
                    else if (minimization is { Minimized: false }) managerMinimized.Remove(window.Id);
                    return window with
                    {
                        Bounds = placement?.Bounds ?? window.Bounds,
                        Visible = visibility?.Visible ?? window.Visible,
                        ManagerHidden = visibility is null ? window.ManagerHidden : !visibility.Visible,
                        Minimized = minimization?.Minimized ?? window.Minimized
                    };
                }).ToArray(),
                FocusedWindowId = plan.FocusWindowId ?? Desktop.FocusedWindowId
            };
        }

        if (NotifyOnApply) Notify();
        activeActions--;
        return Task.FromResult(new ApplyResult(FailActions ? [new(null, "access_denied", "Action rejected.")] : []));
    }

    public Task<ApplyResult> RestoreAsync(CancellationToken cancellationToken)
    {
        Desktop = Desktop with
        {
            Windows = Desktop.Windows.Select(window => window.ManagerHidden || managerMinimized.Contains(window.Id)
                ? window with { Visible = true, ManagerHidden = false, Minimized = false }
                : window).ToArray()
        };
        managerMinimized.Clear();
        return Task.FromResult(new ApplyResult([]));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}