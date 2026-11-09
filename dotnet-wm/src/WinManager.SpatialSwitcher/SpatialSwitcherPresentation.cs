using Avalonia;
using WinManager.Contracts;

namespace WinManager.SpatialSwitcher;

public readonly record struct SwitcherContext(Guid? WorkspaceId, Guid? WindowId, Guid? MonitorId);

public static class SpatialSwitcherPresentation
{
    public static bool HasSpatialState(IReadOnlyList<WorkspaceInfo>? workspaces) =>
        workspaces is { Count: > 0 } && workspaces.All(workspace => workspace.Position is { IsValid: true });

    public static bool Matches(WorkspaceInfo workspace, string? query) =>
        string.IsNullOrWhiteSpace(query) || workspace.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<Guid, Point> MapPositions(IReadOnlyList<WorkspaceInfo> workspaces,
        double width, double height, double cardWidth = 141, double cardHeight = 112, double margin = 18)
    {
        if (!HasSpatialState(workspaces)) return new Dictionary<Guid, Point>();
        var minX = workspaces.Min(workspace => workspace.Position!.X);
        var maxX = workspaces.Max(workspace => workspace.Position!.X);
        var minY = workspaces.Min(workspace => workspace.Position!.Y);
        var maxY = workspaces.Max(workspace => workspace.Position!.Y);
        var availableWidth = Math.Max(0, width - cardWidth - margin * 2);
        var availableHeight = Math.Max(0, height - cardHeight - margin * 2);
        return workspaces.ToDictionary(workspace => workspace.Id, workspace => new Point(
            margin + (maxX == minX
                ? availableWidth / 2
                : (workspace.Position!.X - minX) / (maxX - minX) * availableWidth),
            margin + (maxY == minY
                ? availableHeight / 2
                : (workspace.Position!.Y - minY) / (maxY - minY) * availableHeight)));
    }

    public static Guid? DirectionalTarget(Guid currentId, IReadOnlyList<WorkspaceInfo> workspaces, Direction direction)
    {
        var current = workspaces.FirstOrDefault(workspace => workspace.Id == currentId);
        if (current?.Position is not { IsValid: true } origin) return null;
        return workspaces.Where(workspace => workspace.Id != currentId && workspace.Position is { IsValid: true })
            .Select(workspace => (workspace.Id, workspace.Name, Position: workspace.Position!))
            .Select(candidate =>
            {
                var dx = candidate.Position.X - origin.X;
                var dy = candidate.Position.Y - origin.Y;
                var forward = direction switch
                {
                    Direction.Left => -dx,
                    Direction.Right => dx,
                    Direction.Up => -dy,
                    Direction.Down => dy,
                    _ => 0
                };
                var lateral = direction is Direction.Left or Direction.Right ? Math.Abs(dy) : Math.Abs(dx);
                return (candidate.Id, candidate.Name, Forward: forward,
                    Score: forward > 0 ? forward + lateral * 2 + lateral / forward * 100 : double.PositiveInfinity);
            })
            .Where(candidate => candidate.Forward > 0)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefault();
    }

    public static ManagerCommand ActivationCommand(Guid workspaceId, Guid monitorId)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("A workspace target is required.", nameof(workspaceId));
        if (monitorId == Guid.Empty) throw new ArgumentException("A monitor target is required.", nameof(monitorId));
        return new ManagerCommand(CommandKind.ActivateWorkspace, WorkspaceId: workspaceId, MonitorId: monitorId);
    }

    public static Guid? CurrentWorkspaceId(ManagerSnapshot snapshot)
    {
        var workspaces = snapshot.Workspaces ?? [];
        var focused = snapshot.Desktop.Windows.FirstOrDefault(window =>
            window.Id == snapshot.Desktop.FocusedWindowId);
        return (focused is null ? null : workspaces.FirstOrDefault(workspace =>
                   workspace.WindowIds.Contains(focused.Id))?.Id) ??
               workspaces.FirstOrDefault(workspace => workspace.Active)?.Id ?? workspaces.FirstOrDefault()?.Id;
    }

    public static SwitcherContext CaptureContext(ManagerSnapshot snapshot)
    {
        var workspaceId = CurrentWorkspaceId(snapshot);
        var workspace = snapshot.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        var focused = snapshot.Desktop.Windows.FirstOrDefault(window =>
            window.Id == snapshot.Desktop.FocusedWindowId && workspace?.WindowIds.Contains(window.Id) == true);
        var windows = WindowsForWorkspace(workspace, snapshot.Desktop.Windows);
        var selectedWindow = focused is not null && windows.Any(window => window.Id == focused.Id)
            ? focused
            : windows.FirstOrDefault();
        var monitors = snapshot.Desktop.Monitors;
        var monitorId = selectedWindow is not null && monitors.Any(monitor => monitor.Id == selectedWindow.MonitorId)
            ? selectedWindow.MonitorId
            : workspace is { Active: true } && monitors.Any(monitor => monitor.Id == workspace.MonitorId)
                ? workspace.MonitorId
                : monitors.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
                    .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(monitor => monitor.Id).Select(monitor => (Guid?)monitor.Id).FirstOrDefault();
        return new SwitcherContext(workspaceId, selectedWindow?.Id, monitorId);
    }

    public static IReadOnlyList<WindowInfo> WindowsForWorkspace(WorkspaceInfo? workspace,
        IReadOnlyList<WindowInfo> windows)
    {
        if (workspace is null) return [];
        var membership = workspace.WindowIds.ToHashSet();
        return windows.Where(window => membership.Contains(window.Id) && !window.IsIgnored &&
                                       !window.ManagerHidden && window.Bounds.Width > 0 && window.Bounds.Height > 0 &&
                                       (window.LayoutOverflow || window.Visible && !window.Minimized))
            .ToArray();
    }

    public static Guid? CycleWindow(Guid? currentId, IReadOnlyList<WindowInfo> candidates, bool previous)
    {
        if (candidates.Count == 0) return null;
        var index = currentId is { } id ? Array.FindIndex(candidates.ToArray(), window => window.Id == id) : -1;
        var next = previous
            ? index < 0 ? candidates.Count - 1 : (index + candidates.Count - 1) % candidates.Count
            : (index + 1) % candidates.Count;
        return candidates[next].Id;
    }

    public static Guid? CycleWorkspace(Guid? currentId, IReadOnlyList<WorkspaceInfo> workspaces, bool previous)
    {
        if (workspaces.Count == 0) return null;
        var index = currentId is { } id ? Array.FindIndex(workspaces.ToArray(), workspace => workspace.Id == id) : -1;
        var next = previous
            ? index < 0 ? workspaces.Count - 1 : (index + workspaces.Count - 1) % workspaces.Count
            : (index + 1) % workspaces.Count;
        return workspaces[next].Id;
    }

    public static ManagerCommand FocusCommand(Guid windowId)
    {
        if (windowId == Guid.Empty) throw new ArgumentException("A window target is required.", nameof(windowId));
        return new ManagerCommand(CommandKind.Focus, WindowId: windowId);
    }
}