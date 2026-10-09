using System.Text.Json;
using System.Threading.Channels;
using WinManager.Contracts;

namespace WinManager.Core;

public sealed class ManagerEngine
{
    private abstract record ManagerInput;
    private sealed record ReconcileInput : ManagerInput;
    private sealed record MoveInput(NativeNotification Notification) : ManagerInput;
    private sealed record CommandInput(ManagerCommand Command, CancellationToken Cancellation,
        TaskCompletionSource<CommandResult> Completion) : ManagerInput;
    private sealed record SubscribeInput(IManagerEventSink Sink, IReadOnlyList<string>? Types,
        TaskCompletionSource<SubscriptionResult> Completion) : ManagerInput;
    private sealed record UnsubscribeInput(Guid Id, TaskCompletionSource<CommandResult> Completion) : ManagerInput;
    private sealed record Subscriber(IManagerEventSink Sink, HashSet<string>? Types);
    private readonly Dictionary<Guid, Subscriber> subscribers = [];
    private long sequence;
    public static IReadOnlySet<string> EventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "snapshot", "window.managed", "window.unmanaged", "window.updated", "focus.changed",
        "monitor.added", "monitor.removed", "monitor.updated", "workspace.activated", "workspace.deactivated",
        "workspace.updated", "layout.changed", "pause.changed", "application.exiting"
    };
    private readonly IWindowPlatform platform;
    private readonly Channel<ManagerInput> inputs;
    private readonly WorkspaceSet workspaces;
    private readonly Dictionary<Guid, WindowMode> modes = [];
    private readonly Dictionary<Guid, PixelRect> movingWindows = [];
    private readonly HashSet<Guid> resizingWindows = [];
    private readonly Guid instanceId = Guid.NewGuid();
    private readonly int innerGap;
    private readonly int outerGap;
    private int dirty;
    private bool paused;
    private bool exiting;
    private long revision;
    private string? committedSignature;
    private ManagerSnapshot? current;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public Task Stopped => stopped.Task;
    public ManagerSnapshot? Current => Volatile.Read(ref current);
    public event Action<ManagerSnapshot>? Changed;
    public IReadOnlyList<NativeActionFailure> ShutdownFailures { get; private set; } = [];

    public ManagerEngine(IWindowPlatform platform, int innerGap = 8, int outerGap = 8, int capacity = 256,
        IEnumerable<WorkspaceDefinition>? workspaceDefinitions = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(innerGap);
        ArgumentOutOfRangeException.ThrowIfNegative(outerGap);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.platform = platform;
        this.innerGap = innerGap;
        this.outerGap = outerGap;
        workspaces = new(workspaceDefinitions);
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

    public async Task<SubscriptionResult> SubscribeAsync(IManagerEventSink sink, IReadOnlyList<string>? eventTypes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        if (eventTypes is not null && (eventTypes.Count > EventTypes.Count || eventTypes.Any(type => !EventTypes.Contains(type))))
            return new(false, null, "invalid_argument", Current?.StateRevision ?? 0);
        var completion = new TaskCompletionSource<SubscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inputs.Writer.TryWrite(new SubscribeInput(sink, eventTypes?.ToArray(), completion)))
            return new(false, null, exiting ? "exiting" : "busy", Current?.StateRevision ?? 0);
        // Admission commits even if the caller disconnects; transport must unsubscribe its resulting ID.
        return await completion.Task;
    }

    public async Task<CommandResult> UnsubscribeAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
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
                        try { command.Completion.TrySetResult(await HandleAsync(command.Command, linked.Token)); }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested)
                        { command.Completion.TrySetCanceled(linked.Token); throw; }
                        catch (Exception error)
                        { command.Completion.TrySetResult(new(false, "platform_failure", error.Message, revision)); }
                    }
                }
                else if (input is SubscribeInput subscribe)
                {
                    var id = Guid.NewGuid();
                    var initial = Envelope("snapshot", Current!);
                    if (PublishTo(subscribe.Sink, initial))
                    {
                        subscribers.Add(id, new(subscribe.Sink, subscribe.Types is null ? null : new(subscribe.Types, StringComparer.Ordinal)));
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
                        var bounds = move.Notification.Bounds ?? Current?.Desktop.Windows.FirstOrDefault(window => window.Id == windowId)?.Bounds;
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
            try { await Task.WhenAll(notifications, periodic); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally
            {
                using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { ShutdownFailures = (await platform.RestoreAsync(restoreTimeout.Token)).Failures; }
                catch (Exception error) { ShutdownFailures = [new(null, "restore_failed", error.Message)]; }
                if (Current is not null)
                {
                    try { Commit(ApplyModes(await platform.CaptureAsync(restoreTimeout.Token))); }
                    catch (Exception) { /* Keep the last committed observation if final capture is unavailable. */ }
                    Publish("application.exiting", Current!);
                }
                subscribers.Clear();
                if (Current is null) ready.TrySetException(new InvalidOperationException("Manager failed to initialize."));
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
        Windows = desktop.Windows.Select(window => modes.TryGetValue(window.Id, out var mode)
            ? window with { Mode = mode } : window).ToArray()
    };

    private void ReconcileTrees(DesktopSnapshot desktop)
    {
        foreach (var id in modes.Keys.Except(desktop.Windows.Select(window => window.Id)).ToArray()) modes.Remove(id);
        foreach (var id in movingWindows.Keys.Except(desktop.Windows.Select(window => window.Id)).ToArray()) movingWindows.Remove(id);
        resizingWindows.IntersectWith(movingWindows.Keys);
        workspaces.Reconcile(desktop);
    }

    private async Task<IReadOnlyList<NativeActionFailure>> ReconcileAsync(CancellationToken cancellationToken, Guid? focus = null,
        Guid? droppedWindowId = null)
    {
        var desktop = ApplyModes(await platform.CaptureAsync(cancellationToken));
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
        if (!paused)
        {
            foreach (var workspace in workspaces.Snapshot.Where(workspace => workspace.Active))
            {
                var monitor = desktop.Monitors.SingleOrDefault(monitor => monitor.Id == workspace.MonitorId);
                if (monitor is null) continue;
                try { placements.AddRange(workspaces.Trees[workspace.Id].Layout(monitor, innerGap, outerGap).Placements); }
                catch (InvalidOperationException error) { failures.Add(new(null, "layout_constraints", error.Message)); }
                foreach (var window in desktop.Windows.Where(window => workspace.WindowIds.Contains(window.Id) &&
                    window.Mode == WindowMode.Floating && (window.Visible || window.ManagerHidden) && !window.Minimized && !window.Fullscreen))
                {
                    var area = monitor.WorkArea;
                    var bounds = window.Bounds;
                    var width = Math.Min(bounds.Width, area.Width);
                    var height = Math.Min(bounds.Height, area.Height);
                    if (width < window.MinimumWidth || height < window.MinimumHeight)
                    {
                        failures.Add(new(window.Id, "layout_constraints", "Floating window minimum size exceeds the work area."));
                        continue;
                    }
                    var x = window.MonitorId == monitor.Id ? bounds.X : area.X + (area.Width - width) / 2;
                    var y = window.MonitorId == monitor.Id ? bounds.Y : area.Y + (area.Height - height) / 2;
                    var target = new PixelRect(Math.Clamp(x, area.X, area.X + area.Width - width),
                        Math.Clamp(y, area.Y, area.Y + area.Height - height), width, height);
                    if (target != bounds) placements.Add(new(window.Id, target));
                }
            }
        }
        var changes = placements.Where(placement => desktop.Windows.Any(window =>
            window.Id == placement.WindowId && window.Bounds != placement.Bounds) && !movingWindows.ContainsKey(placement.WindowId)).ToArray();
        var visibility = workspaces.Visibility(desktop).Where(change => !movingWindows.ContainsKey(change.WindowId)).ToArray();
        if (changes.Length > 0 || focus is not null || visibility.Length > 0 || droppedWindowId is not null)
        {
            failures.AddRange((await platform.ApplyAsync(new(changes, focus, visibility,
                droppedWindowId is { } completed ? [completed] : null), cancellationToken)).Failures);
            desktop = ApplyModes(await platform.CaptureAsync(cancellationToken));
            ReconcileTrees(desktop);
        }
        Commit(desktop);
        return failures;
    }

    private void ReflowResize(DesktopSnapshot desktop, Guid windowId, PixelRect original)
    {
        var window = desktop.Windows.FirstOrDefault(window => window.Id == windowId);
        if (window is null || !window.Visible || window.Minimized || window.Fullscreen || window.Mode != WindowMode.Tiling)
            return;
        var bounds = window.Bounds;
        var widthChanged = bounds.Width != original.Width;
        var heightChanged = bounds.Height != original.Height;
        if ((widthChanged || heightChanged) &&
            (!widthChanged || bounds.X == original.X || (long)bounds.X + bounds.Width == (long)original.X + original.Width) &&
            (!heightChanged || bounds.Y == original.Y || (long)bounds.Y + bounds.Height == (long)original.Y + original.Height))
            resizingWindows.Add(windowId);
        if (!resizingWindows.Contains(windowId)) return;
        var workspace = workspaces.Snapshot.FirstOrDefault(workspace => workspace.Active && workspace.WindowIds.Contains(windowId));
        var monitor = desktop.Monitors.FirstOrDefault(monitor => monitor.Id == workspace?.MonitorId);
        if (workspace is null || monitor is null) return;
        try { workspaces.Trees[workspace.Id].ResizeToBounds(windowId, window.Bounds, monitor, innerGap, outerGap); }
        catch (InvalidOperationException) { /* The normal layout pass reports unsatisfied constraints. */ }
    }

    private void ReflowDrop(DesktopSnapshot desktop, Guid windowId, PixelRect original)
    {
        var window = desktop.Windows.FirstOrDefault(window => window.Id == windowId);
        if (window is null || !window.Visible || window.Minimized || window.Fullscreen ||
            (window.Bounds.X == original.X && window.Bounds.Y == original.Y)) return;
        var x = window.Bounds.X + window.Bounds.Width / 2.0;
        var y = window.Bounds.Y + window.Bounds.Height / 2.0;
        var monitor = desktop.Monitors.FirstOrDefault(monitor => x >= monitor.Bounds.X && y >= monitor.Bounds.Y &&
            x < (long)monitor.Bounds.X + monitor.Bounds.Width && y < (long)monitor.Bounds.Y + monitor.Bounds.Height)
            ?? desktop.Monitors.FirstOrDefault(monitor => monitor.Id == window.MonitorId);
        if (monitor is null) return;
        var workspace = workspaces.Snapshot.FirstOrDefault(workspace => workspace.Active && workspace.MonitorId == monitor.Id);
        if (workspace is null) return;
        Guid? target = null;
        if (window.Mode == WindowMode.Tiling)
        {
            try
            {
                target = workspaces.Trees[workspace.Id].Layout(monitor, innerGap, outerGap).Placements
                    .OrderBy(placement => DistanceToTile(placement.Bounds, x, y)).ThenBy(placement => placement.WindowId)
                    .FirstOrDefault()?.WindowId;
            }
            catch (InvalidOperationException) { /* The normal layout pass reports unsatisfied constraints. */ }
        }
        workspaces.MoveWindow(windowId, workspace.Id);
        if (target is { } targetId && targetId != windowId)
            workspaces.Trees[workspace.Id].Swap(windowId, targetId);
    }

    private static double DistanceToTile(PixelRect bounds, double x, double y)
    {
        var dx = Math.Max(Math.Max(bounds.X - x, x - ((long)bounds.X + bounds.Width)), 0);
        var dy = Math.Max(Math.Max(bounds.Y - y, y - ((long)bounds.Y + bounds.Height)), 0);
        return dx * dx + dy * dy;
    }

    private void Commit(DesktopSnapshot desktop)
    {
        var next = new ManagerSnapshot(instanceId, 0, desktop,
            workspaces.Trees.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot), paused, workspaces.Snapshot);
        var signature = JsonSerializer.Serialize(next);
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
        catch (Exception) { }
        try { sink.Disconnect("slow_client"); } catch (Exception) { }
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
        foreach (var id in oldWindows.Keys.Except(newWindows.Keys)) Publish("window.unmanaged", next);
        foreach (var (id, window) in newWindows)
        {
            if (!oldWindows.TryGetValue(id, out var old)) Publish("window.managed", next);
            else if (old != window) Publish("window.updated", next);
        }
        if (previous?.Desktop.FocusedWindowId != next.Desktop.FocusedWindowId) Publish("focus.changed", next);
        var oldMonitors = previous?.Desktop.Monitors.ToDictionary(monitor => monitor.Id) ?? [];
        var newMonitors = next.Desktop.Monitors.ToDictionary(monitor => monitor.Id);
        foreach (var id in oldMonitors.Keys.Except(newMonitors.Keys)) Publish("monitor.removed", next);
        foreach (var (id, monitor) in newMonitors)
        {
            if (!oldMonitors.TryGetValue(id, out var old)) Publish("monitor.added", next);
            else if (old != monitor) Publish("monitor.updated", next);
        }
        var oldWorkspaces = previous?.Workspaces?.ToDictionary(workspace => workspace.Id) ?? [];
        foreach (var workspace in next.Workspaces ?? [])
        {
            if (!oldWorkspaces.TryGetValue(workspace.Id, out var old)) Publish("workspace.updated", next);
            else if (old.Name != workspace.Name || old.MonitorId != workspace.MonitorId ||
                !old.WindowIds.SequenceEqual(workspace.WindowIds)) Publish("workspace.updated", next);
            if (old?.Active == true && !workspace.Active) Publish("workspace.deactivated", next);
            if (old?.Active != true && workspace.Active) Publish("workspace.activated", next);
        }
        if (previous is null || JsonSerializer.Serialize(previous.Trees) != JsonSerializer.Serialize(next.Trees) ||
            newWindows.Any(pair => !oldWindows.TryGetValue(pair.Key, out var old) || old.Bounds != pair.Value.Bounds))
            Publish("layout.changed", next);
        if (previous?.Paused != next.Paused) Publish("pause.changed", next);
    }

    private async Task<CommandResult> HandleAsync(ManagerCommand command, CancellationToken cancellationToken)
    {
        if (command.WorkspaceName is { } name)
        {
            if (command.WorkspaceId is not null || string.IsNullOrWhiteSpace(name) ||
                command.Kind is not (CommandKind.ActivateWorkspace or CommandKind.MoveToWorkspace))
                return Error("invalid_argument", "Specify exactly one workspace name or ID for a workspace command.");
            var named = workspaces.Snapshot.SingleOrDefault(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase));
            if (named is null) return Error("not_found", "Workspace name does not exist.");
            command = command with { WorkspaceId = named.Id };
        }
        if (!Enum.IsDefined(command.Kind) ||
            command.Direction is { } direction && !Enum.IsDefined(direction) ||
            command.Mode is { } mode && !Enum.IsDefined(mode) ||
            command.Orientation is { } orientation && !Enum.IsDefined(orientation))
            return Error("invalid_argument", "Unknown command or enum value.");
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
            if (command.WorkspaceId is not { } workspaceId) return Error("invalid_argument", "Workspace ID is required.");
            var previous = workspaces.Snapshot.ToArray();
            if (previous.Any(workspace => workspace.Id == workspaceId && workspace.Active)) return new(true, null, null, revision);
            if (!workspaces.Activate(workspaceId)) return Error("not_found", "Workspace is not assigned to a monitor.");
            var failures = await ReconcileAsync(cancellationToken);
            if (failures.Count > 0)
            {
                foreach (var workspace in previous.Where(workspace => workspace.Active)) workspaces.Activate(workspace.Id);
                await ReconcileAsync(cancellationToken);
            }
            return Result(failures);
        }
        else if (command.Kind != CommandKind.Redraw)
        {
            var desktop = Current!.Desktop;
            var window = desktop.Windows.SingleOrDefault(window => window.Id == (command.WindowId ?? desktop.FocusedWindowId));
            if (window is null) return Error("not_found", "Target window is not managed.");
            var assignedWorkspace = workspaces.WorkspaceFor(window.Id);
            if (assignedWorkspace is not { } assigned || !workspaces.Trees.TryGetValue(assigned, out var tree))
                return Error("not_found", "Target workspace disappeared.");
            switch (command.Kind)
            {
                case CommandKind.Focus:
                {
                    var target = command.Direction is { } focusDirection
                        ? tree.Neighbor(window.Id, focusDirection, desktop.Windows) : window.Id;
                    if (target is null) return Error("not_found", "No directional focus target.");
                    var failures = await ReconcileAsync(cancellationToken, target);
                    return Result(failures);
                }
                case CommandKind.Move:
                    if (command.Direction is not { } moveDirection) return Error("invalid_argument", "Move requires direction.");
                    var neighbor = tree.Neighbor(window.Id, moveDirection, desktop.Windows.Where(candidate =>
                        workspaces.WorkspaceFor(candidate.Id) == assigned && candidate.Mode == WindowMode.Tiling).ToArray());
                    if (neighbor is null || !tree.Swap(window.Id, neighbor.Value)) return Error("not_found", "No tiling movement target.");
                    break;
                case CommandKind.Resize:
                    if (command.Direction is not { } resizeDirection || command.Amount is not { } amount ||
                        !double.IsFinite(amount) || amount <= 0 || amount > 0.8)
                        return Error("invalid_argument", "Resize requires direction and a fraction in (0, 0.8].");
                    if (!tree.Resize(window.Id, resizeDirection, amount)) return Error("not_found", "No matching split to resize.");
                    break;
                case CommandKind.SetSplit:
                    if (command.Orientation is not { } split) return Error("invalid_argument", "Split requires orientation.");
                    tree.SetOrientation(window.Id, split);
                    break;
                case CommandKind.SetMode:
                    if (command.Mode is not { } requestedMode) return Error("invalid_argument", "Mode requires tiling or floating.");
                    modes[window.Id] = requestedMode;
                    break;
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
                default: return Error("invalid_argument", "Unsupported command.");
            }
        }
        return Result(await ReconcileAsync(cancellationToken));
    }

    private CommandResult Result(IReadOnlyList<NativeActionFailure> failures) => failures.Count == 0
        ? new(true, null, null, revision)
        : new(false, failures[0].Code, string.Join("; ", failures.Select(failure => failure.Message)), revision);
    private CommandResult Error(string code, string message) => new(false, code, message, revision);
}