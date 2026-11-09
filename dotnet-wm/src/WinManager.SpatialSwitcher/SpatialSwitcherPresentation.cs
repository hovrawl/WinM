using Avalonia;
using WinManager.Contracts;

namespace WinManager.SpatialSwitcher;

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
}