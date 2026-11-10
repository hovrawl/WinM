using System.Text.Json;
using System.Threading.Channels;
using WinManager.Contracts;

namespace WinManager.Core;

public sealed class ManagerEngine
{
    private abstract record ManagerInput;

    private sealed record ReconcileInput : ManagerInput;

    private sealed record MoveInput(NativeNotification Notification) : ManagerInput;

    private sealed record CommandInput(
        ManagerCommand Command,
        CancellationToken Cancellation,
        TaskCompletionSource<CommandResult> Completion) : ManagerInput;

    private sealed record SubscribeInput(
        IManagerEventSink Sink,
        IReadOnlyList<string>? Types,
        TaskCompletionSource<SubscriptionResult> Completion) : ManagerInput;

    private sealed record UnsubscribeInput(Guid Id, TaskCompletionSource<CommandResult> Completion) : ManagerInput;

    private sealed record Subscriber(IManagerEventSink Sink, HashSet<string>? Types);

    private readonly Dictionary<Guid, Subscriber> subscribers = [];
    private long sequence;

    public static IReadOnlySet<string> EventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ManagerEventTypes.Snapshot, ManagerEventTypes.WindowManaged, ManagerEventTypes.WindowUnmanaged,
        ManagerEventTypes.WindowUpdated, ManagerEventTypes.FocusChanged, ManagerEventTypes.MonitorAdded,
        ManagerEventTypes.MonitorRemoved, ManagerEventTypes.MonitorUpdated, ManagerEventTypes.WorkspaceActivated,
        ManagerEventTypes.WorkspaceDeactivated, ManagerEventTypes.WorkspaceUpdated, ManagerEventTypes.LayoutChanged,
        ManagerEventTypes.PauseChanged, ManagerEventTypes.ApplicationExiting, ManagerEventTypes.SelectionChanged
    };

    private readonly IWindowPlatform platform;
    private readonly Channel<ManagerInput> inputs;
    private readonly WorkspaceSet workspaces;
    private readonly string[] monitorDeviceOrder;
    private readonly Dictionary<Guid, WindowMode> modes = [];
    private readonly Dictionary<Guid, PixelRect> floatingBounds = [];
    private readonly Dictionary<Guid, PixelRect> movingWindows = [];
    private readonly HashSet<Guid> resizingWindows = [];
    private readonly HashSet<Guid> overflowWindowIds = [];
    private readonly HashSet<Guid> managerMinimizedWindowIds = [];
    private readonly Dictionary<Guid, HashSet<Guid>> stagedWindowIds = [];
    private readonly Dictionary<Guid, Guid> lastFocusedWindowByWorkspace = [];
    private readonly Dictionary<Guid, Guid[]> appliedLayeredStackOrders = [];
    private readonly Guid instanceId = Guid.NewGuid();
    private readonly int innerGap;
    private readonly int outerGap;
    private int dirty;
    private bool paused;
    private bool exiting;
    private ContainerSelection? selection;
    private long revision;
    private string? committedSignature;
    private IReadOnlyList<NativeActionFailure> reportedFailures = [];
    private ManagerSnapshot? current;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public Task Stopped => stopped.Task;
    public ManagerSnapshot? Current => Volatile.Read(ref current);
    public event Action<ManagerSnapshot>? Changed;
    public event Action<IReadOnlyList<NativeActionFailure>>? ActionsFailed;
    public IReadOnlyList<NativeActionFailure> ShutdownFailures { get; private set; } = [];

    public ManagerEngine(IWindowPlatform platform, int innerGap = 8, int outerGap = 8, int capacity = 256,
        IEnumerable<WorkspaceDefinition>? workspaceDefinitions = null,
        IEnumerable<string>? monitorDeviceOrder = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(innerGap);
        ArgumentOutOfRangeException.ThrowIfNegative(outerGap);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.platform = platform;
        this.innerGap = innerGap;
        this.outerGap = outerGap;
        workspaces = new(workspaceDefinitions);
        this.monitorDeviceOrder = monitorDeviceOrder?.ToArray() ?? [];
        if (this.monitorDeviceOrder.Length > 10 ||
            this.monitorDeviceOrder.Any(string.IsNullOrWhiteSpace) ||
            this.monitorDeviceOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != this.monitorDeviceOrder.Length)
            throw new ArgumentException("Monitor device order must contain at most ten unique names.",
                nameof(monitorDeviceOrder));
        inputs = Channel.CreateBounded<ManagerInput>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async Task<CommandResult> ExecuteAsync(ManagerCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inputs.Writer.TryWrite(new CommandInput(command, cancellationToken, completion)))
            return new(false, exiting ? "exiting" : "busy", "Manager cannot admit this request.", revision);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task<SubscriptionResult> SubscribeAsync(IManagerEventSink sink,
        IReadOnlyList<string>? eventTypes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        if (eventTypes is not null &&
            (eventTypes.Count > EventTypes.Count || eventTypes.Any(type => !EventTypes.Contains(type))))
            return new(false, null, "invalid_argument", Current?.StateRevision ?? 0);
        var completion =
            new TaskCompletionSource<SubscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inputs.Writer.TryWrite(new SubscribeInput(sink, eventTypes?.ToArray(), completion)))
            return new(false, null, exiting ? "exiting" : "busy", Current?.StateRevision ?? 0);
        // Admission commits even if the caller disconnects; transport must unsubscribe its resulting ID.
        return await completion.Task;
    }

    public async Task<CommandResult> UnsubscribeAsync(Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inputs.Writer.TryWrite(new UnsubscribeInput(subscriptionId, completion)))
            return Error(exiting ? "exiting" : "busy", "Manager cannot admit unsubscribe.");
        return await completion.Task;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task notifications = Task.CompletedTask;
        Task periodic = Task.CompletedTask;
        try
        {
            await platform.StartAsync(linked.Token);
            notifications = PumpNotificationsAsync(linked.Token);
            periodic = RescanAsync(linked.Token);
            await ReconcileAsync(linked.Token);
            while (!exiting)
            {
                var read = inputs.Reader.ReadAsync(linked.Token).AsTask();
                var completed = await Task.WhenAny(read, notifications, periodic);
                if (completed != read)
                {
                    await completed;
                    throw new InvalidOperationException("Desktop notification source stopped unexpectedly.");
                }

                var input = await read;
                if (input is CommandInput command)
                {
                    if (command.Cancellation.IsCancellationRequested)
                        command.Completion.TrySetCanceled(command.Cancellation);
                    else
                    {
                        try
                        {
                            command.Completion.TrySetResult(await HandleAsync(command.Command, linked.Token));
                        }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested)
                        {
                            command.Completion.TrySetCanceled(linked.Token);
                            throw;
                        }
                        catch (Exception error)
                        {
                            command.Completion.TrySetResult(new(false, "platform_failure", error.Message, revision));
                        }
                    }
                }
                else if (input is SubscribeInput subscribe)
                {
                    var id = Guid.NewGuid();
                    var initial = Envelope(ManagerEventTypes.Snapshot, Current!);
                    if (PublishTo(subscribe.Sink, initial))
                    {
                        subscribers.Add(id,
                            new(subscribe.Sink,
                                subscribe.Types is null ? null : new(subscribe.Types, StringComparer.Ordinal)));
                        subscribe.Completion.TrySetResult(new(true, id, null, revision));
                    }
                    else subscribe.Completion.TrySetResult(new(false, null, "slow_client", revision));
                }
                else if (input is UnsubscribeInput unsubscribe)
                {
                    subscribers.Remove(unsubscribe.Id);
                    unsubscribe.Completion.TrySetResult(new(true, null, null, revision));
                }
                else if (input is MoveInput move && move.Notification.WindowId is { } windowId)
                {
                    if (move.Notification.Kind == NativeNotificationKind.MoveStarted)
                    {
                        var bounds = move.Notification.Bounds ?? Current?.Desktop.Windows
                            .FirstOrDefault(window => window.Id == windowId)?.Bounds;
                        if (bounds is not null) movingWindows.TryAdd(windowId, bounds);
                        await ReconcileAsync(linked.Token);
                    }
                    else
                        await ReconcileAsync(linked.Token, droppedWindowId: windowId);
                }
                else await ReconcileAsync(linked.Token);

                if (Interlocked.Exchange(ref dirty, 0) != 0 && !exiting)
                    await ReconcileAsync(linked.Token);
            }
        }
        finally
        {
            exiting = true;
            inputs.Writer.TryComplete();
            await linked.CancelAsync();
            while (inputs.Reader.TryRead(out var pending))
            {
                if (pending is CommandInput command)
                    command.Completion.TrySetResult(new(false, "exiting", "Manager stopped.", revision));
                else if (pending is SubscribeInput subscribe)
                    subscribe.Completion.TrySetResult(new(false, null, "exiting", revision));
                else if (pending is UnsubscribeInput unsubscribe)
                    unsubscribe.Completion.TrySetResult(new(true, null, null, revision));
            }

            try
            {
                await Task.WhenAll(notifications, periodic);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
            finally
            {
                using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    ShutdownFailures = (await platform.RestoreAsync(restoreTimeout.Token)).Failures;
                }
                catch (Exception error)
                {
                    ShutdownFailures = [new(null, "restore_failed", error.Message)];
                }

                if (Current is not null)
                {
                    try
                    {
                        Commit(ApplyModes(await platform.CaptureAsync(restoreTimeout.Token)));
                    }
                    catch (Exception)
                    {
                        /* Keep the last committed observation if final capture is unavailable. */
                    }

                    Publish(ManagerEventTypes.ApplicationExiting, Current!);
                }

                subscribers.Clear();
                if (Current is null)
                    ready.TrySetException(new InvalidOperationException("Manager failed to initialize."));
                stopped.TrySetResult();
            }
        }
    }

    private void RequestReconcile()
    {
        if (Interlocked.Exchange(ref dirty, 1) == 0)
            inputs.Writer.TryWrite(new ReconcileInput());
    }

    private async Task PumpNotificationsAsync(CancellationToken cancellationToken)
    {
        await foreach (var notification in platform.Notifications(cancellationToken))
        {
            if (notification.Kind is NativeNotificationKind.MoveStarted or NativeNotificationKind.MoveEnded)
                await inputs.Writer.WriteAsync(new MoveInput(notification), cancellationToken);
            else RequestReconcile();
        }
    }

    private async Task RescanAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken)) RequestReconcile();
    }

    private DesktopSnapshot ApplyModes(DesktopSnapshot desktop) => desktop with
    {
        Windows = desktop.Windows.Select(window =>
        {
            var mode = modes.TryGetValue(window.Id, out var selectedMode)
                ? selectedMode
                : window.IsIgnored
                    ? WindowMode.Floating
                    : window.Mode;
            return window with
            {
                Mode = mode,
                TilingOverride = modes.ContainsKey(window.Id) ? mode == WindowMode.Tiling : window.TilingOverride,
                LayoutOverflow = overflowWindowIds.Contains(window.Id)
            };
        }).ToArray()
    };

    private void ReconcileTrees(DesktopSnapshot desktop)
    {
        var existing = desktop.Windows.Select(window => window.Id).ToHashSet();
        foreach (var id in modes.Keys.Except(existing).ToArray()) modes.Remove(id);
        foreach (var id in floatingBounds.Keys.Except(existing).ToArray()) floatingBounds.Remove(id);
        foreach (var id in movingWindows.Keys.Except(existing).ToArray()) movingWindows.Remove(id);
        overflowWindowIds.IntersectWith(existing);
        managerMinimizedWindowIds.IntersectWith(existing);
        var observed = desktop.Windows.ToDictionary(window => window.Id);
        managerMinimizedWindowIds.RemoveWhere(id => !observed[id].Minimized);
        foreach (var staged in stagedWindowIds.Values) staged.IntersectWith(existing);
        resizingWindows.IntersectWith(movingWindows.Keys);
        workspaces.Reconcile(desktop);
        var workspaceIds = workspaces.Snapshot.Select(workspace => workspace.Id).ToHashSet();
        foreach (var workspaceId in appliedLayeredStackOrders.Keys.Where(id => !workspaceIds.Contains(id)).ToArray())
            appliedLayeredStackOrders.Remove(workspaceId);
        foreach (var (workspaceId, windowId) in lastFocusedWindowByWorkspace.ToArray())
            if (workspaces.WorkspaceFor(windowId) != workspaceId)
                lastFocusedWindowByWorkspace.Remove(workspaceId);
    }

    private Guid? PreviouslyFocusedTile(WorkspaceInfo workspace, DesktopSnapshot desktop)
    {
        if (desktop.FocusedWindowId is { } focused && IsStagedTile(workspace, desktop, focused)) return focused;
        return lastFocusedWindowByWorkspace.TryGetValue(workspace.Id, out var remembered) &&
               IsStagedTile(workspace, desktop, remembered)
            ? remembered
            : null;
    }

    private bool IsStagedTile(WorkspaceInfo workspace, DesktopSnapshot desktop, Guid id) =>
        workspace.WindowIds.Contains(id) && workspaces.WorkspaceFor(id) == workspace.Id &&
        stagedWindowIds.TryGetValue(workspace.Id, out var staged) && staged.Contains(id) &&
        workspaces.Trees[workspace.Id].NodeForWindow(id) is not null &&
        desktop.Windows.FirstOrDefault(window => window.Id == id) is
            { Visible: true, Minimized: false, LayoutOverflow: false, IsIgnored: false, Mode: WindowMode.Tiling };

    private void RememberFocusedStagedWindow(DesktopSnapshot desktop)
    {
        if (desktop.FocusedWindowId is not { } focused) return;
        var workspace = workspaces.Snapshot.FirstOrDefault(candidate =>
            candidate.Active && candidate.WindowIds.Contains(focused));
        if (workspace is not null && IsStagedTile(workspace, desktop, focused))
            lastFocusedWindowByWorkspace[workspace.Id] = focused;
    }

    private async Task<IReadOnlyList<NativeActionFailure>> ReconcileAsync(CancellationToken cancellationToken,
        Guid? focus = null,
        Guid? droppedWindowId = null, Guid? restoreOverflowWindowId = null,
        Guid? restoreFocusedWindowId = null,
        (Guid WindowId, WindowMode Mode)? modeChange = null,
        (Guid WindowId, PixelRect Bounds)? floatingPlacement = null)
    {
        var desktop = ApplyModes(await platform.CaptureAsync(cancellationToken));
        var restoreFocusedWindowWasMinimized = restoreFocusedWindowId is { } restoreId &&
            desktop.Windows.Any(window => window.Id == restoreId && window.Minimized);
        if (restoreFocusedWindowWasMinimized)
            desktop = desktop with
            {
                Windows = desktop.Windows.Select(window => window.Id == restoreFocusedWindowId
                    ? window with { Visible = true, Minimized = false }
                    : window).ToArray()
            };

        foreach (var window in desktop.Windows.Where(window =>
                     window.Visible && !window.Minimized && !window.ManagerHidden &&
                     window.Bounds.Width > 0 && window.Bounds.Height > 0))
        {
            if (window.Mode == WindowMode.Floating || !floatingBounds.ContainsKey(window.Id))
                floatingBounds[window.Id] = window.Bounds;
        }

        Guid? restoreFloatingWindowId = null;
        if (modeChange is { } change)
        {
            if (change.Mode == WindowMode.Floating && desktop.Windows.Any(window => window.Id == change.WindowId &&
                    window.Mode == WindowMode.Tiling && !window.IsIgnored))
                restoreFloatingWindowId = change.WindowId;
            modes[change.WindowId] = change.Mode;
            desktop = ApplyModes(desktop);
        }

        ReconcileTrees(desktop);
        if (!paused)
            foreach (var (id, originalBounds) in movingWindows)
                ReflowResize(desktop, id, originalBounds);
        if (droppedWindowId is { } dropped && movingWindows.Remove(dropped, out var original))
        {
            var resized = resizingWindows.Remove(dropped);
            if (!paused && !resized) ReflowDrop(desktop, dropped, original);
        }

        List<NativeActionFailure> failures = [];
        List<WindowPlacement> placements = [];
        List<Guid> stackingOrderWindowIds = [];
        Dictionary<Guid, Guid[]> changedLayeredStackOrders = [];
        var restoreTargetStaged = false;
        if (!paused)
        {
            var activeWorkspaces = workspaces.Snapshot.Where(workspace => workspace.Active).ToArray();
            var activeWindowIds = activeWorkspaces.SelectMany(workspace => workspace.WindowIds).ToHashSet();
            var windowsById = desktop.Windows.ToDictionary(window => window.Id);
            var nextOverflow = overflowWindowIds.Where(id => !activeWindowIds.Contains(id) &&
                                                             windowsById.TryGetValue(id, out var window) &&
                                                             window.Mode == WindowMode.Tiling && !window.IsIgnored)
                .ToHashSet();
            foreach (var workspace in activeWorkspaces)
            {
                var monitor = desktop.Monitors.SingleOrDefault(monitor => monitor.Id == workspace.MonitorId);
                if (monitor is null) continue;
                try
                {
                    var priority = new List<Guid>();
                    var restoreTarget = restoreOverflowWindowId ?? restoreFocusedWindowId;
                    if (restoreTarget is { } restoring && workspace.WindowIds.Contains(restoring))
                        priority.Add(restoring);
                    var displacedFocus = restoreTarget is not null
                        ? PreviouslyFocusedTile(workspace, desktop)
                        : null;
                    if (stagedWindowIds.TryGetValue(workspace.Id, out var staged))
                    {
                        priority.AddRange(staged.Where(id => id != displacedFocus && id != restoreTarget));
                        if (displacedFocus is { } displaced) priority.Add(displaced);
                    }

                    var preferredFocus = restoreTarget is { } && workspace.WindowIds.Contains(restoreTarget.Value)
                        ? restoreTarget
                        : focus ?? desktop.FocusedWindowId;
                    var layeredFocus = preferredFocus is { } preferredId && workspace.WindowIds.Contains(preferredId) &&
                                       windowsById.TryGetValue(preferredId, out var preferredWindow) &&
                                       preferredWindow.Mode == WindowMode.Tiling && !preferredWindow.IsIgnored &&
                                       (preferredWindow.Visible && !preferredWindow.Minimized ||
                                        preferredWindow.LayoutOverflow)
                        ? preferredId
                        : PreviouslyFocusedTile(workspace, desktop);
                    var plan = Layout(workspace, monitor, windowsById, priority);
                    if (restoreTarget is { } target && workspace.WindowIds.Contains(target) &&
                        plan.Placements.Any(placement => placement.WindowId == target))
                    {
                        restoreTargetStaged = true;
                        if (displacedFocus is { } previous &&
                            !plan.Placements.Any(placement => placement.WindowId == previous) &&
                            workspaces.Trees[workspace.Id].NodeForWindow(target) is not null &&
                            workspaces.Trees[workspace.Id].NodeForWindow(previous) is not null)
                        {
                            workspaces.Trees[workspace.Id].Swap(target, previous);
                            plan = Layout(workspace, monitor, windowsById, priority);
                        }
                    }

                    placements.AddRange(plan.Placements);
                    if (workspace.Layout == WorkspaceLayout.Layered)
                    {
                        var stackOrder = OrderLayeredStack(plan.Placements, layeredFocus,
                            appliedLayeredStackOrders.GetValueOrDefault(workspace.Id));
                        var layeredPlacementsChanged = plan.Placements.Any(placement =>
                            windowsById[placement.WindowId].Bounds != placement.Bounds);
                        var moveCompleted = droppedWindowId is { } completedMoveId &&
                                            workspace.WindowIds.Contains(completedMoveId);
                        if (stackOrder.Length == 0)
                            appliedLayeredStackOrders.Remove(workspace.Id);
                        else if (layeredPlacementsChanged || moveCompleted ||
                                 !appliedLayeredStackOrders.TryGetValue(workspace.Id, out var applied) ||
                                 !applied.SequenceEqual(stackOrder))
                        {
                            stackingOrderWindowIds.AddRange(stackOrder);
                            changedLayeredStackOrders[workspace.Id] = stackOrder;
                        }
                    }
                    else
                        appliedLayeredStackOrders.Remove(workspace.Id);
                    nextOverflow.ExceptWith(workspace.WindowIds);
                    nextOverflow.UnionWith(plan.OverflowWindowIds ?? []);
                    stagedWindowIds[workspace.Id] = plan.Placements.Select(placement => placement.WindowId).ToHashSet();
                }
                catch (InvalidOperationException error)
                {
                    failures.Add(new(null, "layout_constraints", error.Message));
                }

                foreach (var window in desktop.Windows.Where(window => workspace.WindowIds.Contains(window.Id) &&
                                                                       window.Mode == WindowMode.Floating &&
                                                                       (window.Visible || window.ManagerHidden) &&
                                                                       !window.Minimized && !window.IsIgnored))
                {
                    var area = monitor.WorkArea;
                    var bounds = floatingPlacement is { } explicitPlacement &&
                                 explicitPlacement.WindowId == window.Id
                        ? explicitPlacement.Bounds
                        : window.Id == restoreFloatingWindowId &&
                                 floatingBounds.TryGetValue(window.Id, out var saved)
                        ? saved
                        : window.Bounds;
                    var width = Math.Min(bounds.Width, area.Width);
                    var height = Math.Min(bounds.Height, area.Height);
                    if (width < window.MinimumWidth || height < window.MinimumHeight)
                    {
                        failures.Add(new(window.Id, "layout_constraints",
                            "Floating window minimum size exceeds the work area."));
                        continue;
                    }

                    var x = window.MonitorId == monitor.Id ? bounds.X : area.X + (area.Width - width) / 2;
                    var y = window.MonitorId == monitor.Id ? bounds.Y : area.Y + (area.Height - height) / 2;
                    var target = new PixelRect(Math.Clamp(x, area.X, area.X + area.Width - width),
                        Math.Clamp(y, area.Y, area.Y + area.Height - height), width, height);
                    if (target != window.Bounds) placements.Add(new(window.Id, target));
                }
            }

            overflowWindowIds.Clear();
            overflowWindowIds.UnionWith(nextOverflow);
        }

        if ((restoreOverflowWindowId ?? restoreFocusedWindowId) is { } requestedRestore &&
            !restoreTargetStaged && focus == requestedRestore)
        {
            focus = null;
            failures.Add(new(requestedRestore, "layout_constraints",
                "Window does not fit the active workspace layout."));
        }

        if (restoreFocusedWindowId is not null && !restoreTargetStaged)
            desktop = ApplyModes(await platform.CaptureAsync(cancellationToken));

        desktop = ApplyModes(desktop);
        var changes = placements.Where(placement => desktop.Windows.Any(window =>
                                                        window.Id == placement.WindowId &&
                                                        window.Bounds != placement.Bounds) &&
                                                    !movingWindows.ContainsKey(placement.WindowId)).ToArray();
        var visibility = workspaces.Visibility(desktop).Where(change => !movingWindows.ContainsKey(change.WindowId))
            .ToArray();
        var minimizations = desktop.Windows.Where(window =>
        {
            if (window.Id == restoreFocusedWindowId) return restoreTargetStaged && restoreFocusedWindowWasMinimized;
            if (overflowWindowIds.Contains(window.Id)) return !window.Minimized;
            return managerMinimizedWindowIds.Contains(window.Id) && window.Minimized;
        }).Select(window => new WindowMinimization(window.Id, overflowWindowIds.Contains(window.Id))).ToArray();
        if (changes.Length > 0 || focus is not null || visibility.Length > 0 || minimizations.Length > 0 ||
            droppedWindowId is not null || stackingOrderWindowIds.Count > 0)
        {
            var applyResult = await platform.ApplyAsync(new(changes, focus, visibility,
                droppedWindowId is { } completed ? [completed] : null, minimizations,
                StackingOrderWindowIds: stackingOrderWindowIds.Count == 0 ? null : stackingOrderWindowIds),
                cancellationToken);
            failures.AddRange(applyResult.Failures);
            foreach (var (workspaceId, stackOrder) in changedLayeredStackOrders)
                if (!applyResult.Failures.Any(failure => failure.WindowId is null ||
                        stackOrder.Contains(failure.WindowId.Value)) && !stackOrder.Any(movingWindows.ContainsKey))
                    appliedLayeredStackOrders[workspaceId] = stackOrder;
            desktop = ApplyModes(await platform.CaptureAsync(cancellationToken));
            var observed = desktop.Windows.ToDictionary(window => window.Id);
            foreach (var minimization in minimizations)
            {
                if (!observed.TryGetValue(minimization.WindowId, out var window)) continue;
                if (minimization.Minimized && window.Minimized) managerMinimizedWindowIds.Add(window.Id);
                else if (!minimization.Minimized && !window.Minimized) managerMinimizedWindowIds.Remove(window.Id);
                else if (!minimization.Minimized && managerMinimizedWindowIds.Contains(window.Id))
                    overflowWindowIds.Add(window.Id);
            }

            desktop = ApplyModes(desktop);
            ReconcileTrees(desktop);
        }

        Commit(desktop);
        RememberFocusedStagedWindow(desktop);
        var diagnostics = failures.ToArray();
        if (!reportedFailures.SequenceEqual(diagnostics))
        {
            reportedFailures = diagnostics;
            if (reportedFailures.Count > 0) ActionsFailed?.Invoke(reportedFailures);
        }

        return failures;
    }

    private void ReflowResize(DesktopSnapshot desktop, Guid windowId, PixelRect original)
    {
        var window = desktop.Windows.FirstOrDefault(window => window.Id == windowId);
        if (window is null || !window.Visible || window.Minimized || window.IsIgnored ||
            window.Mode != WindowMode.Tiling)
            return;
        var bounds = window.Bounds;
        var widthChanged = bounds.Width != original.Width;
        var heightChanged = bounds.Height != original.Height;
        if ((widthChanged || heightChanged) &&
            (!widthChanged || bounds.X == original.X ||
             (long)bounds.X + bounds.Width == (long)original.X + original.Width) &&
            (!heightChanged || bounds.Y == original.Y ||
             (long)bounds.Y + bounds.Height == (long)original.Y + original.Height))
            resizingWindows.Add(windowId);
        if (!resizingWindows.Contains(windowId)) return;
        var workspace =
            workspaces.Snapshot.FirstOrDefault(workspace => workspace.Active && workspace.WindowIds.Contains(windowId));
        var monitor = desktop.Monitors.FirstOrDefault(monitor => monitor.Id == workspace?.MonitorId);
        if (workspace is null || monitor is null) return;
        try
        {
            workspaces.Trees[workspace.Id].ResizeToBounds(windowId, window.Bounds, monitor, innerGap, outerGap);
        }
        catch (InvalidOperationException)
        {
            /* The normal layout pass reports unsatisfied constraints. */
        }
    }

    private void ReflowDrop(DesktopSnapshot desktop, Guid windowId, PixelRect original)
    {
        var window = desktop.Windows.FirstOrDefault(window => window.Id == windowId);
        if (window is null || !window.Visible || window.Minimized || window.IsIgnored ||
            (window.Bounds.X == original.X && window.Bounds.Y == original.Y)) return;
        var x = window.Bounds.X + window.Bounds.Width / 2.0;
        var y = window.Bounds.Y + window.Bounds.Height / 2.0;
        var monitor = desktop.Monitors.FirstOrDefault(monitor => x >= monitor.Bounds.X && y >= monitor.Bounds.Y &&
                                                                 x < (long)monitor.Bounds.X + monitor.Bounds.Width &&
                                                                 y < (long)monitor.Bounds.Y + monitor.Bounds.Height)
                      ?? desktop.Monitors.FirstOrDefault(monitor => monitor.Id == window.MonitorId);
        if (monitor is null) return;
        var workspace =
            workspaces.Snapshot.FirstOrDefault(workspace => workspace.Active && workspace.MonitorId == monitor.Id);
        if (workspace is null) return;
        Guid? target = null;
        if (window.Mode == WindowMode.Tiling)
        {
            try
            {
                target = Layout(workspace, monitor, desktop.Windows.ToDictionary(candidate => candidate.Id)).Placements
                    .OrderBy(placement => DistanceToTile(placement.Bounds, x, y))
                    .ThenBy(placement => placement.WindowId)
                    .FirstOrDefault()?.WindowId;
            }
            catch (InvalidOperationException)
            {
                /* The normal layout pass reports unsatisfied constraints. */
            }
        }

        workspaces.MoveWindow(windowId, workspace.Id);
        if (target is { } targetId && targetId != windowId)
            workspaces.Trees[workspace.Id].Swap(windowId, targetId);
    }

    private LayoutPlan Layout(WorkspaceInfo workspace, MonitorInfo monitor,
        IReadOnlyDictionary<Guid, WindowInfo> windowsById, IReadOnlyList<Guid>? priorityOrder = null)
    {
        var tree = workspaces.Trees[workspace.Id];
        return workspace.Layout switch
        {
            WorkspaceLayout.Split => tree.Layout(monitor, innerGap, outerGap, priorityOrder),
            WorkspaceLayout.Layered => LayeredLayout.Layout(monitor,
                tree.WindowOrder.Select(windowId => windowsById[windowId]).ToArray(), outerGap,
                workspace.LayeredLayoutOptions),
            _ => throw new InvalidOperationException("Workspace layout is not supported.")
        };
    }

    private static Guid[] OrderLayeredStack(IReadOnlyList<WindowPlacement> placements, Guid? focusedWindowId,
        IReadOnlyList<Guid>? previousOrder)
    {
        var windowIds = placements.Select(placement => placement.WindowId).ToArray();
        if (windowIds.Length == 0) return windowIds;

        var focusedIndex = Array.IndexOf(windowIds, focusedWindowId);
        if (focusedIndex >= 0)
            return windowIds.Select((windowId, index) => (windowId, index))
                .OrderBy(item => Math.Abs(item.index - focusedIndex))
                .ThenBy(item => item.index)
                .Select(item => item.windowId).ToArray();

        if (previousOrder is null) return windowIds;
        var previousIndices = previousOrder.Select((windowId, index) => (windowId, index))
            .ToDictionary(item => item.windowId, item => item.index);
        return windowIds.Select((windowId, index) => (windowId, index))
            .OrderBy(item => previousIndices.TryGetValue(item.windowId, out var previousIndex)
                ? previousIndex
                : int.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.windowId).ToArray();
    }

    private static double DistanceToTile(PixelRect bounds, double x, double y)
    {
        var dx = Math.Max(Math.Max(bounds.X - x, x - ((long)bounds.X + bounds.Width)), 0);
        var dy = Math.Max(Math.Max(bounds.Y - y, y - ((long)bounds.Y + bounds.Height)), 0);
        return dx * dx + dy * dy;
    }

    private void Commit(DesktopSnapshot desktop)
    {
        if (selection is { } selected &&
            (Current?.Desktop.FocusedWindowId != desktop.FocusedWindowId ||
             !workspaces.Snapshot.Any(workspace => workspace.Id == selected.WorkspaceId && workspace.Active) ||
             !workspaces.Trees.TryGetValue(selected.WorkspaceId, out var selectedTree) ||
             selectedTree.FindNode(selected.NodeId) is null))
            selection = null;
        var next = new ManagerSnapshot(instanceId, 0, desktop,
            workspaces.Trees.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot), paused,
            workspaces.Snapshot, selection, workspaces.Views);
        var signature = JsonSerializer.Serialize(next, Protocol.TypeInfo<ManagerSnapshot>());
        if (signature == committedSignature) return;
        committedSignature = signature;
        next = next with { StateRevision = ++revision };
        var previous = Current;
        Volatile.Write(ref current, next);
        ready.TrySetResult();
        Changed?.Invoke(next);
        PublishChanges(previous, next);
    }

    private EventEnvelope Envelope(string type, ManagerSnapshot snapshot) =>
        new(Protocol.Version, instanceId, ++sequence, snapshot.StateRevision, type, Protocol.Payload(snapshot));

    private static bool PublishTo(IManagerEventSink sink, EventEnvelope envelope)
    {
        try
        {
            if (sink.TryPublish(envelope)) return true;
        }
        catch (Exception)
        {
        }

        try
        {
            sink.Disconnect("slow_client");
        }
        catch (Exception)
        {
        }

        return false;
    }

    private void Publish(string type, ManagerSnapshot snapshot)
    {
        var envelope = Envelope(type, snapshot);
        foreach (var (id, subscriber) in subscribers.ToArray())
        {
            if (subscriber.Types is not null && !subscriber.Types.Contains(type)) continue;
            if (!PublishTo(subscriber.Sink, envelope)) subscribers.Remove(id);
        }
    }

    private void PublishChanges(ManagerSnapshot? previous, ManagerSnapshot next)
    {
        var oldWindows = previous?.Desktop.Windows.ToDictionary(window => window.Id) ?? [];
        var newWindows = next.Desktop.Windows.ToDictionary(window => window.Id);
        foreach (var id in oldWindows.Keys.Except(newWindows.Keys)) Publish(ManagerEventTypes.WindowUnmanaged, next);
        foreach (var (id, window) in newWindows)
        {
            if (!oldWindows.TryGetValue(id, out var old)) Publish(ManagerEventTypes.WindowManaged, next);
            else if (old != window) Publish(ManagerEventTypes.WindowUpdated, next);
        }

        if ((previous?.Desktop.ConfiguredExclusions is null) != (next.Desktop.ConfiguredExclusions is null) ||
            !(previous?.Desktop.ConfiguredExclusions ?? []).SequenceEqual(next.Desktop.ConfiguredExclusions ?? []))
            Publish(ManagerEventTypes.WindowUpdated, next);
        if (previous?.Desktop.FocusedWindowId != next.Desktop.FocusedWindowId)
            Publish(ManagerEventTypes.FocusChanged, next);
        if (previous?.Selection != next.Selection) Publish(ManagerEventTypes.SelectionChanged, next);
        var oldMonitors = previous?.Desktop.Monitors.ToDictionary(monitor => monitor.Id) ?? [];
        var newMonitors = next.Desktop.Monitors.ToDictionary(monitor => monitor.Id);
        foreach (var id in oldMonitors.Keys.Except(newMonitors.Keys)) Publish(ManagerEventTypes.MonitorRemoved, next);
        foreach (var (id, monitor) in newMonitors)
        {
            if (!oldMonitors.TryGetValue(id, out var old)) Publish(ManagerEventTypes.MonitorAdded, next);
            else if (old != monitor) Publish(ManagerEventTypes.MonitorUpdated, next);
        }

        var oldWorkspaces = previous?.Workspaces?.ToDictionary(workspace => workspace.Id) ?? [];
        foreach (var workspace in next.Workspaces ?? [])
        {
            if (!oldWorkspaces.TryGetValue(workspace.Id, out var old))
                Publish(ManagerEventTypes.WorkspaceUpdated, next);
            else if (old.Name != workspace.Name || old.MonitorId != workspace.MonitorId ||
                     old.Layout != workspace.Layout || old.Position != workspace.Position ||
                     old.LayeredLayoutOptions != workspace.LayeredLayoutOptions ||
                     !old.WindowIds.SequenceEqual(workspace.WindowIds))
                Publish(ManagerEventTypes.WorkspaceUpdated, next);
            if (old?.Active == true && !workspace.Active) Publish(ManagerEventTypes.WorkspaceDeactivated, next);
            if (old?.Active != true && workspace.Active) Publish(ManagerEventTypes.WorkspaceActivated, next);
        }

        if (previous is null || JsonSerializer.Serialize(previous.Trees,
                Protocol.TypeInfo<IReadOnlyDictionary<Guid, SplitNodeInfo?>>()) !=
            JsonSerializer.Serialize(next.Trees, Protocol.TypeInfo<IReadOnlyDictionary<Guid, SplitNodeInfo?>>()) ||
            newWindows.Any(pair => !oldWindows.TryGetValue(pair.Key, out var old) || old.Bounds != pair.Value.Bounds))
            Publish(ManagerEventTypes.LayoutChanged, next);
        if (previous?.Paused != next.Paused) Publish(ManagerEventTypes.PauseChanged, next);
    }

    private async Task<CommandResult> HandleAsync(ManagerCommand command, CancellationToken cancellationToken)
    {
        if (command.WorkspaceNumber is { } workspaceNumber)
        {
            if (command.Kind != CommandKind.MoveToWorkspace || command.WorkspaceId is not null ||
                command.WorkspaceName is not null || workspaceNumber is < 1 or > 10)
                return Error("invalid_argument", "Workspace number is only valid from 1 through 10 on moveToWorkspace.");
            var numberedWorkspace = workspaces.Snapshot.ElementAtOrDefault(workspaceNumber - 1);
            if (numberedWorkspace is null) return Error("not_found", "Numbered workspace does not exist.");
            command = command with { WorkspaceId = numberedWorkspace.Id, WorkspaceNumber = null };
        }

        if (command.WorkspaceName is { } name)
        {
            if (command.WorkspaceId is not null || string.IsNullOrWhiteSpace(name) ||
                command.Kind is not (CommandKind.ActivateWorkspace or CommandKind.MoveToWorkspace or
                    CommandKind.FocusCycle or CommandKind.SetWorkspaceLayout or CommandKind.SetWorkspacePosition))
                return Error("invalid_argument", "Specify exactly one workspace name or ID for a workspace command.");
            var named = workspaces.Snapshot.SingleOrDefault(workspace =>
                string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase));
            if (named is null) return Error("not_found", "Workspace name does not exist.");
            command = command with { WorkspaceId = named.Id };
        }

        if (!Enum.IsDefined(command.Kind) ||
            command.Direction is { } direction && !Enum.IsDefined(direction) ||
            command.Mode is { } mode && !Enum.IsDefined(mode) ||
            command.Orientation is { } orientation && !Enum.IsDefined(orientation) ||
            command.Navigation is { } navigation && !Enum.IsDefined(navigation) ||
            command.CycleDirection is { } cycleDirection && !Enum.IsDefined(cycleDirection) ||
            command.Layout is { } layout && !Enum.IsDefined(layout) ||
            command.Position is { IsValid: false } ||
            command.LayeredLayoutOptions is { IsValid: false })
            return Error("invalid_argument", "Unknown command or enum value.");
        if (command.MonitorId is not null &&
            command.Kind is not (CommandKind.ActivateWorkspace or CommandKind.MoveToMonitor))
            return Error("invalid_argument", "Monitor ID is only supported by activateWorkspace and moveToMonitor.");
        if (command.MonitorNumber is { } monitorNumber &&
            (command.Kind != CommandKind.FocusMonitor || monitorNumber is < 1 or > 10))
            return Error("invalid_argument", "Monitor number is only valid from 1 through 10 on focusMonitor.");
        if (command.Kind == CommandKind.FocusMonitor && command.MonitorNumber is null)
            return Error("invalid_argument", "focusMonitor requires a monitor number.");
        if (command.Kind != CommandKind.FocusMonitor && command.MonitorNumber is not null)
            return Error("invalid_argument", "Monitor number is only supported by focusMonitor.");
        if (command.Kind == CommandKind.FocusMonitor &&
            (command.WindowId is not null || command.Direction is not null || command.Amount is not null ||
             command.Orientation is not null || command.Mode is not null || command.Paused is not null ||
             command.WorkspaceId is not null || command.WorkspaceName is not null || command.WorkspaceNumber is not null ||
             command.MonitorId is not null || command.NodeId is not null || command.Navigation is not null ||
             command.CycleDirection is not null || command.Layout is not null || command.Position is not null ||
             command.LayeredLayoutOptions is not null))
            return Error("invalid_argument", "focusMonitor accepts only a monitor number.");
        if (command.Kind != CommandKind.SelectContainer &&
            (command.NodeId is not null || command.Navigation is not null))
            return Error("invalid_argument", "Node ID and navigation are only supported by selectContainer.");
        if (command.Kind != CommandKind.FocusCycle && command.CycleDirection is not null)
            return Error("invalid_argument", "Cycle direction is only supported by focusCycle.");
        if (command.Kind != CommandKind.SetWorkspaceLayout && command.Layout is not null)
            return Error("invalid_argument", "Layout is only supported by setWorkspaceLayout.");
        if (command.Kind != CommandKind.SetWorkspaceLayout && command.LayeredLayoutOptions is not null)
            return Error("invalid_argument", "Layered layout options are only supported by setWorkspaceLayout.");
        if (command.Kind != CommandKind.SetWorkspacePosition && command.Position is not null)
            return Error("invalid_argument", "Position is only supported by setWorkspacePosition.");
        if (command.Kind == CommandKind.FocusCycle)
            return await FocusCycleAsync(command, cancellationToken);
        if (command.Kind == CommandKind.FocusMonitor)
            return await FocusMonitorAsync(command.MonitorNumber!.Value, cancellationToken);
        if (command.Kind == CommandKind.SelectContainer)
        {
            if (command.NodeId is not null && (command.Navigation is not null || command.WindowId is not null))
                return Error("invalid_argument", "Specify a node ID or window/navigation target, not both.");
            ContainerSelection? target = null;
            if (command.NodeId is { } nodeId)
            {
                var workspace = workspaces.Snapshot.FirstOrDefault(workspace => workspace.Active &&
                    workspaces.Trees[workspace.Id].FindNode(nodeId) is not null);
                if (workspace is not null) target = new(workspace.Id, nodeId);
            }
            else
            {
                var origin = command.WindowId is null && command.Navigation is not null ? selection : null;
                if (origin is null && (command.WindowId ?? Current!.Desktop.FocusedWindowId) is { } windowId &&
                    workspaces.WorkspaceFor(windowId) is { } workspaceId &&
                    workspaces.Snapshot.Any(workspace => workspace.Id == workspaceId && workspace.Active) &&
                    workspaces.Trees[workspaceId].NodeForWindow(windowId) is { } leaf)
                    origin = new(workspaceId, leaf.Id);
                if (origin is not null)
                {
                    var node = command.Navigation is { } navigate
                        ? workspaces.Trees[origin.WorkspaceId].Navigate(origin.NodeId, navigate)
                        : workspaces.Trees[origin.WorkspaceId].FindNode(origin.NodeId);
                    if (node is not null) target = new(origin.WorkspaceId, node.Id);
                }
            }

            if (target is null) return Error("not_found", "No active tiling container matches the selection.");
            selection = target;
            Commit(Current!.Desktop);
            return new(true, null, null, revision);
        }

        if (command.Kind == CommandKind.ClearSelection)
        {
            selection = null;
            Commit(Current!.Desktop);
            return new(true, null, null, revision);
        }

        if (command.Kind == CommandKind.SetSplit && command.WindowId is null && selection is { } selected)
        {
            if (command.Orientation is not { } split) return Error("invalid_argument", "Split requires orientation.");
            if (!workspaces.Trees[selected.WorkspaceId].SetContainerOrientation(selected.NodeId, split))
                return Error("not_found", "Selected container is not a split.");
            return Result(await ReconcileAsync(cancellationToken));
        }

        if (command.Kind == CommandKind.Exit)
        {
            exiting = true;
            return new(true, null, null, revision);
        }

        if (command.Kind == CommandKind.Pause)
        {
            if (command.Paused is null) return Error("invalid_argument", "Pause requires a boolean.");
            paused = command.Paused.Value;
        }
        else if (command.Kind == CommandKind.ActivateWorkspace)
        {
            if (command.WorkspaceId is not { } workspaceId)
                return Error("invalid_argument", "Workspace ID is required.");
            if (!workspaces.Snapshot.Any(workspace => workspace.Id == workspaceId))
                return Error("not_found", "Workspace does not exist.");
            var desktop = Current!.Desktop;
            var monitorId = command.MonitorId ?? ResolveTargetMonitor(desktop);
            if (monitorId == Guid.Empty)
                return Error("not_ready", "No connected monitor is available for a workspace view.");
            if (!desktop.Monitors.Any(monitor => monitor.Id == monitorId))
                return Error("not_found", "Target monitor is not connected.");
            if (workspaces.Views.Any(view => view.MonitorId == monitorId && view.WorkspaceId == workspaceId))
                return new(true, null, null, revision);
            var previous = workspaces.CaptureState();
            if (!workspaces.Activate(workspaceId, monitorId))
                return Error("not_found", "Workspace view could not be assigned to the target monitor.");
            var failures = await ReconcileAsync(cancellationToken);
            if (failures.Count > 0)
            {
                workspaces.RestoreState(previous);
                await ReconcileAsync(cancellationToken);
            }

            return Result(failures);
        }
        else if (command.Kind == CommandKind.SetWorkspaceLayout)
        {
            if (command.WorkspaceId is not { } workspaceId || workspaceId == Guid.Empty ||
                command.Layout is not { } requestedLayout)
                return Error("invalid_argument", "setWorkspaceLayout requires a workspace target and layout.");
            if (command.WindowId is not null || command.MonitorId is not null || command.Mode is not null ||
                command.Paused is not null ||
                command.Direction is not null || command.Amount is not null || command.Orientation is not null ||
                command.NodeId is not null || command.Navigation is not null || command.CycleDirection is not null)
                return Error("invalid_argument", "setWorkspaceLayout accepts only a workspace target and layout.");
            var previousWorkspace = workspaces.Snapshot.SingleOrDefault(workspace => workspace.Id == workspaceId);
            if (previousWorkspace is null) return Error("not_found", "Workspace does not exist.");
            var requestedOptions = command.LayeredLayoutOptions ?? previousWorkspace.LayeredLayoutOptions;
            if (previousWorkspace.Layout == requestedLayout &&
                previousWorkspace.LayeredLayoutOptions == requestedOptions) return new(true, null, null, revision);
            workspaces.SetLayout(workspaceId, requestedLayout, requestedOptions);
            var failures = await ReconcileAsync(cancellationToken);
            if (failures.Count > 0)
            {
                workspaces.SetLayout(workspaceId, previousWorkspace.Layout, previousWorkspace.LayeredLayoutOptions);
                await ReconcileAsync(cancellationToken);
            }

            return Result(failures);
        }
        else if (command.Kind == CommandKind.SetWorkspacePosition)
        {
            if (command.WorkspaceId is not { } workspaceId || workspaceId == Guid.Empty ||
                command.Position is not { IsValid: true } position)
                return Error("invalid_argument",
                    "setWorkspacePosition requires a workspace target and valid position.");
            if (command.WindowId is not null || command.MonitorId is not null || command.Mode is not null ||
                command.Paused is not null || command.Direction is not null || command.Amount is not null ||
                command.Orientation is not null || command.NodeId is not null || command.Navigation is not null ||
                command.CycleDirection is not null || command.Layout is not null)
                return Error("invalid_argument", "setWorkspacePosition accepts only a workspace target and position.");
            var workspace = workspaces.Snapshot.SingleOrDefault(candidate => candidate.Id == workspaceId);
            if (workspace is null) return Error("not_found", "Workspace does not exist.");
            if (workspace.Position == position) return new(true, null, null, revision);
            if (!workspaces.SetPosition(workspaceId, position))
                return Error("invalid_argument", "Workspace position is outside the supported desk bounds.");
            Commit(Current!.Desktop);
            return new(true, null, null, revision);
        }
        else if (command.Kind != CommandKind.Redraw)
        {
            var desktop = Current!.Desktop;
            var window =
                desktop.Windows.SingleOrDefault(window => window.Id == (command.WindowId ?? desktop.FocusedWindowId));
            if (window is null) return Error("not_found", "Target window is not managed.");
            var assignedWorkspace = workspaces.WorkspaceFor(window.Id);
            if (assignedWorkspace is not { } assigned || !workspaces.Trees.TryGetValue(assigned, out var tree))
                return Error("not_found", "Target workspace disappeared.");
            switch (command.Kind)
            {
                case CommandKind.Focus:
                {
                    var target = command.Direction is { } focusDirection
                        ? FindSpatialFocusTarget(window, assigned, focusDirection, desktop)?.Id
                        : window.Id;
                    if (target is null) return Error("not_found", "No directional focus target.");
                    Guid? restoreOverflow = null;
                    Guid? restoreManuallyMinimized = null;
                    if (window.Id == target.Value && window.Mode == WindowMode.Tiling &&
                        (window.LayoutOverflow || window.Minimized))
                    {
                        if (paused)
                            return Error("not_ready", "Resume the manager before restoring an inactive tiled window.");
                        if (!workspaces.Snapshot.Any(workspace => workspace.Id == assigned && workspace.Active))
                            return Error("not_ready",
                                "Activate the workspace view before restoring an inactive tiled window.");
                        if (window.LayoutOverflow) restoreOverflow = window.Id;
                        else restoreManuallyMinimized = window.Id;
                    }

                    var failures = await ReconcileAsync(cancellationToken, target,
                        restoreOverflowWindowId: restoreOverflow,
                        restoreFocusedWindowId: restoreManuallyMinimized);
                    return Result(failures);
                }
                case CommandKind.Move:
                    if (command.Direction is not { } moveDirection)
                        return Error("invalid_argument", "Move requires direction.");
                    var neighbor = tree.Neighbor(window.Id, moveDirection, desktop.Windows.Where(candidate =>
                            workspaces.WorkspaceFor(candidate.Id) == assigned && candidate.Mode == WindowMode.Tiling)
                        .ToArray());
                    if (neighbor is null || !tree.Swap(window.Id, neighbor.Value))
                        return Error("not_found", "No tiling movement target.");
                    break;
                case CommandKind.Resize:
                    if (command.Direction is not { } resizeDirection || command.Amount is not { } amount ||
                        !double.IsFinite(amount) || amount <= 0 || amount > 0.8)
                        return Error("invalid_argument", "Resize requires direction and a fraction in (0, 0.8].");
                    if (!tree.Resize(window.Id, resizeDirection, amount))
                        return Error("not_found", "No matching split to resize.");
                    break;
                case CommandKind.SetSplit:
                    if (command.Orientation is not { } split)
                        return Error("invalid_argument", "Split requires orientation.");
                    tree.SetOrientation(window.Id, split);
                    break;
                case CommandKind.SetMode:
                    if (command.Mode is not { } requestedMode)
                        return Error("invalid_argument", "Mode requires tiling or floating.");
                    return Result(await ReconcileAsync(cancellationToken, modeChange: (window.Id, requestedMode)));
                case CommandKind.ToggleMode:
                    var toggledMode = window.Mode == WindowMode.Tiling && !window.IsIgnored
                        ? WindowMode.Floating
                        : WindowMode.Tiling;
                    return Result(await ReconcileAsync(cancellationToken, modeChange: (window.Id, toggledMode)));
                case CommandKind.MoveToWorkspace:
                case CommandKind.MoveToMonitor:
                    if (command.Kind == CommandKind.MoveToWorkspace && command.WorkspaceId is null ||
                        command.Kind == CommandKind.MoveToMonitor && command.MonitorId is null)
                        return Error("invalid_argument", "Destination ID is required.");
                    if (command.Kind == CommandKind.MoveToWorkspace && command.WorkspaceId == assigned ||
                        command.Kind == CommandKind.MoveToMonitor && workspaces.Snapshot.Any(workspace =>
                            workspace.Id == assigned && workspace.MonitorId == command.MonitorId && workspace.Active))
                        return new(true, null, null, revision);
                    var moved = command.Kind == CommandKind.MoveToWorkspace
                        ? workspaces.MoveWindow(window.Id, command.WorkspaceId!.Value)
                        : workspaces.MoveWindowToMonitor(window.Id, command.MonitorId!.Value);
                    if (!moved) return Error("not_found", "Destination does not exist.");
                    var moveFailures = await ReconcileAsync(cancellationToken);
                    if (moveFailures.Count > 0)
                    {
                        workspaces.MoveWindow(window.Id, assigned);
                        await ReconcileAsync(cancellationToken);
                    }

                    return Result(moveFailures);
                case CommandKind.MoveSpatial:
                    if (command.Direction is not { } spatialDirection || command.WorkspaceId is not null ||
                        command.WorkspaceName is not null || command.MonitorId is not null ||
                        command.MonitorNumber is not null || command.WorkspaceNumber is not null ||
                        command.Amount is not null || command.Orientation is not null || command.Mode is not null ||
                        command.Paused is not null || command.NodeId is not null || command.Navigation is not null ||
                        command.CycleDirection is not null || command.Layout is not null || command.Position is not null ||
                        command.LayeredLayoutOptions is not null)
                        return Error("invalid_argument", "moveSpatial accepts only a window and direction.");
                    return await MoveSpatialAsync(window, assigned, spatialDirection, cancellationToken);
                default: return Error("invalid_argument", "Unsupported command.");
            }
        }

        return Result(await ReconcileAsync(cancellationToken));
    }

    private async Task<CommandResult> FocusCycleAsync(ManagerCommand command, CancellationToken cancellationToken)
    {
        if (command.Direction is not null)
            return Error("invalid_argument", "Use cycleDirection, not direction, for focusCycle.");
        var desktop = Current!.Desktop;
        var anchor =
            desktop.Windows.FirstOrDefault(window => window.Id == (command.WindowId ?? desktop.FocusedWindowId));
        if (command.WindowId is not null && anchor is null)
            return Error("not_found", "Target window is not managed.");
        var workspaceId = command.WorkspaceId ?? (anchor is not null ? workspaces.WorkspaceFor(anchor.Id) : null);
        WorkspaceInfo? workspace;
        if (workspaceId is { } id)
            workspace = workspaces.Snapshot.FirstOrDefault(workspace => workspace.Id == id && workspace.Active);
        else
        {
            var monitor = desktop.Monitors.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
                .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.Id)
                .FirstOrDefault();
            workspace = workspaces.Snapshot.FirstOrDefault(workspace =>
                workspace.Active && workspace.MonitorId == monitor?.Id);
        }

        if (workspace is null) return Error("not_found", "No active workspace matches the focus cycle.");
        if (command.WindowId is not null && !workspace.WindowIds.Contains(command.WindowId.Value))
            return Error("invalid_argument", "Target window must belong to the requested workspace.");
        var order = workspaces.Trees[workspace.Id].WindowOrder.Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);
        var candidates = desktop.Windows.Where(window => workspace.WindowIds.Contains(window.Id) &&
                                                         window.Visible && !window.Minimized && !window.IsIgnored &&
                                                         window.Bounds.Width > 0 && window.Bounds.Height > 0 &&
                                                         (command.Mode is null || window.Mode == command.Mode))
            .OrderBy(window => order.GetValueOrDefault(window.Id, int.MaxValue)).ThenBy(window => window.Id).ToArray();
        if (candidates.Length == 0) return Error("not_found", "No windows match the focus cycle filter.");
        var index = Array.FindIndex(candidates, window => window.Id == anchor?.Id);
        var target = command.CycleDirection == FocusCycleDirection.Previous
            ? candidates[index <= 0 ? candidates.Length - 1 : index - 1].Id
            : candidates[(index + 1) % candidates.Length].Id;
        if (target == desktop.FocusedWindowId) return new(true, null, null, revision);
        return Result(await ReconcileAsync(cancellationToken, target));
    }

    private async Task<CommandResult> FocusMonitorAsync(int monitorNumber, CancellationToken cancellationToken)
    {
        if (monitorNumber is < 1 or > 10)
            return Error("invalid_argument", "Monitor number must be from 1 through 10.");
        var desktop = Current!.Desktop;
        var monitor = ResolveMonitorNumber(desktop, monitorNumber);
        if (monitor is null) return Error("not_found", "Numbered monitor is not connected.");
        var workspaceId = workspaces.Views.FirstOrDefault(view => view.MonitorId == monitor.Id)?.WorkspaceId;
        if (workspaceId is null) return Error("not_found", "Monitor has no workspace view.");
        var candidates = desktop.Windows.Where(window => workspaces.WorkspaceFor(window.Id) == workspaceId &&
                                                          window.MonitorId == monitor.Id &&
                                                          SpatialNavigation.IsEligible(window));
        var target = SpatialNavigation.ClosestWindow(monitor.WorkArea, candidates);
        if (target is null) return Error("not_found", "The numbered monitor has no eligible window to focus.");
        if (desktop.FocusedWindowId == target.Id) return new(true, null, null, revision);
        return Result(await ReconcileAsync(cancellationToken, target.Id));
    }

    private WindowInfo? FindSpatialFocusTarget(WindowInfo origin, Guid workspaceId, Direction direction,
        DesktopSnapshot desktop)
    {
        if (!workspaces.Snapshot.Any(workspace => workspace.Id == workspaceId && workspace.Active)) return null;
        var workspaceWindows = desktop.Windows.Where(window => workspaces.WorkspaceFor(window.Id) == workspaceId &&
                                                                SpatialNavigation.IsEligible(window)).ToArray();
        var local = SpatialNavigation.ClosestWindowInDirection(origin.Bounds, direction, workspaceWindows,
            origin.Id);
        if (local is not null) return local;

        var sourceMonitor = desktop.Monitors.FirstOrDefault(monitor => monitor.Id == origin.MonitorId);
        if (sourceMonitor is null) return null;
        var activeViews = workspaces.Views.ToDictionary(view => view.MonitorId, view => view.WorkspaceId);
        var candidatesByMonitor = desktop.Monitors.Select(monitor => new
            {
                Monitor = monitor,
                Windows = desktop.Windows.Where(window => window.MonitorId == monitor.Id &&
                    activeViews.TryGetValue(monitor.Id, out var viewedWorkspace) &&
                    workspaces.WorkspaceFor(window.Id) == viewedWorkspace && SpatialNavigation.IsEligible(window)).ToArray()
            })
            .Where(item => item.Windows.Length > 0);
        var neighbor = SpatialNavigation.ClosestMonitorInDirection(sourceMonitor.Bounds, direction,
            candidatesByMonitor.Select(item => item.Monitor));
        return neighbor is null
            ? null
            : SpatialNavigation.ClosestWindow(origin.Bounds,
                candidatesByMonitor.First(item => item.Monitor.Id == neighbor.Id).Windows);
    }

    private async Task<CommandResult> MoveSpatialAsync(WindowInfo window, Guid sourceWorkspaceId, Direction direction,
        CancellationToken cancellationToken)
    {
        if (paused) return Error("not_ready", "Resume the manager before spatially moving a window.");
        if (!SpatialNavigation.IsEligible(window))
            return Error("not_ready", "Only a visible, active, non-overflow managed window can be moved spatially.");
        var sourceWorkspace = workspaces.Snapshot.FirstOrDefault(workspace =>
            workspace.Id == sourceWorkspaceId && workspace.Active);
        if (sourceWorkspace is null) return Error("not_ready", "The window's workspace view is inactive.");

        var desktop = Current!.Desktop;
        var sourceMonitor = desktop.Monitors.FirstOrDefault(monitor => monitor.Id == sourceWorkspace.MonitorId);
        if (sourceMonitor is null) return Error("not_found", "The window's monitor is not connected.");
        var localWindows = desktop.Windows.Where(candidate => workspaces.WorkspaceFor(candidate.Id) == sourceWorkspaceId);
        var localTarget = SpatialNavigation.ClosestWindowInDirection(window.Bounds, direction, localWindows, window.Id);
        Guid targetWorkspaceId = sourceWorkspaceId;
        PixelRect targetBounds;
        try
        {
            if (localTarget is not null)
            {
                targetBounds = SpatialNavigation.PlaceAt(window, sourceMonitor.WorkArea,
                    localTarget.Bounds.X, localTarget.Bounds.Y);
            }
            else
            {
                var target = FindSpatialFocusTarget(window, sourceWorkspaceId, direction, desktop);
                if (target is null) return Error("not_found", "No window exists in that direction.");
                targetWorkspaceId = workspaces.WorkspaceFor(target.Id)!.Value;
                var targetMonitor = desktop.Monitors.FirstOrDefault(monitor => monitor.Id == target.MonitorId);
                if (targetMonitor is null) return Error("not_found", "The neighboring monitor is not connected.");
                targetBounds = SpatialNavigation.CenterIn(window, targetMonitor.WorkArea);
            }
        }
        catch (InvalidOperationException error)
        {
            return Error("layout_constraints", error.Message);
        }

        var movedWorkspace = targetWorkspaceId != sourceWorkspaceId;
        if (movedWorkspace && !workspaces.MoveWindow(window.Id, targetWorkspaceId))
            return Error("not_found", "The neighboring workspace is no longer available.");
        var hadModeOverride = modes.TryGetValue(window.Id, out var previousModeOverride);
        var failures = await ReconcileAsync(cancellationToken, window.Id,
            modeChange: (window.Id, WindowMode.Floating), floatingPlacement: (window.Id, targetBounds));
        if (failures.Count == 0) return Result(failures);

        if (movedWorkspace) workspaces.MoveWindow(window.Id, sourceWorkspaceId);
        if (hadModeOverride) modes[window.Id] = previousModeOverride;
        else modes.Remove(window.Id);
        await ReconcileAsync(cancellationToken);
        return Result(failures);
    }

    private MonitorInfo? ResolveMonitorNumber(DesktopSnapshot desktop, int monitorNumber)
    {
        var stable = desktop.Monitors.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.Id).ToArray();
        if (monitorDeviceOrder.Length == 0) return stable.ElementAtOrDefault(monitorNumber - 1);
        if (monitorNumber <= monitorDeviceOrder.Length)
            return stable.FirstOrDefault(monitor => string.Equals(monitor.DeviceName,
                monitorDeviceOrder[monitorNumber - 1], StringComparison.OrdinalIgnoreCase));
        var configuredNames = monitorDeviceOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return stable.Where(monitor => !configuredNames.Contains(monitor.DeviceName))
            .ElementAtOrDefault(monitorNumber - monitorDeviceOrder.Length - 1);
    }

    private static Guid ResolveTargetMonitor(DesktopSnapshot desktop)
    {
        var connected = desktop.Monitors.ToDictionary(monitor => monitor.Id);
        if (desktop.FocusedWindowId is { } focusedId &&
            desktop.Windows.FirstOrDefault(window => window.Id == focusedId) is { } focused &&
            connected.ContainsKey(focused.MonitorId)) return focused.MonitorId;
        return desktop.Monitors.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.Id)
            .Select(monitor => monitor.Id).FirstOrDefault();
    }

    private CommandResult Result(IReadOnlyList<NativeActionFailure> failures) => failures.Count == 0
        ? new(true, null, null, revision)
        : new(false, failures[0].Code, string.Join("; ", failures.Select(failure => failure.Message)), revision);

    private CommandResult Error(string code, string message) => new(false, code, message, revision);
}