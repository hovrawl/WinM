using System.Threading.Channels;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Core.Tests;

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
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Floating), stop.Token)).Success);
        Assert.Equal(WindowMode.Floating, engine.Current!.Desktop.Windows.Single(window => window.Id == id).Mode);
        fake.Plans.Clear();
        Assert.True((await engine.ExecuteAsync(new(CommandKind.Redraw), stop.Token)).Success);
        Assert.DoesNotContain(fake.Plans.SelectMany(plan => plan.Placements), placement => placement.WindowId == id);
        Assert.True((await engine.ExecuteAsync(new(CommandKind.SetMode, id, Mode: WindowMode.Tiling), stop.Token)).Success);
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
        fake.Desktop = fake.Desktop with { Windows = fake.Desktop.Windows.Select(window =>
            window with { Bounds = new(200, 200, 200, 200) }).ToArray() };
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
        Assert.Equal("not_found", (await engine.ExecuteAsync(new(CommandKind.Focus, Guid.NewGuid()), stop.Token)).ErrorCode);
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
}

internal sealed class FakeWindowPlatform : IWindowPlatform
{
    private readonly Channel<NativeNotification> notifications = Channel.CreateBounded<NativeNotification>(1);
    private int activeActions;
    public DesktopSnapshot Desktop { get; set; }
    public bool FailActions { get; set; }
    public bool NotifyOnApply { get; set; }
    public int MaximumConcurrentActions { get; private set; }
    public List<LayoutPlan> Plans { get; } = [];
    public FakeWindowPlatform()
    {
        var monitorId = Guid.NewGuid();
        Desktop = new([new(monitorId, "display", new(-1000, 0, 1000, 900), new(-1000, 0, 1000, 850), 96)],
            Enumerable.Range(0, 2).Select(index => new WindowInfo(Guid.NewGuid(), monitorId, $"Window {index}",
                "test", "test", new(-900 + index * 300, 50, 200, 200), true, false, false, WindowMode.Tiling)).ToArray(), null);
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<DesktopSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(Desktop);
    public IAsyncEnumerable<NativeNotification> Notifications(CancellationToken cancellationToken) =>
        notifications.Reader.ReadAllAsync(cancellationToken);
    public void Notify() => notifications.Writer.TryWrite(new(NativeNotificationKind.Reconcile));
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
                    return window with
                    {
                        Bounds = placement?.Bounds ?? window.Bounds,
                        Visible = visibility?.Visible ?? window.Visible,
                        ManagerHidden = visibility is null ? window.ManagerHidden : !visibility.Visible
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
        Desktop = Desktop with { Windows = Desktop.Windows.Select(window => window.ManagerHidden
            ? window with { Visible = true, ManagerHidden = false } : window).ToArray() };
        return Task.FromResult(new ApplyResult([]));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}