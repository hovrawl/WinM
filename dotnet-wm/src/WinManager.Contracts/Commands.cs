namespace WinManager.Contracts;

public enum Direction { Left, Right, Up, Down }
public enum SplitOrientation { Horizontal, Vertical }
public enum ContainerNavigation { Parent, FirstChild, SecondChild, Sibling, Root }
public enum CommandKind { Focus, Move, Resize, SetSplit, SetMode, Pause, Redraw, Exit, ActivateWorkspace, MoveToWorkspace, MoveToMonitor, ToggleMode, SelectContainer, ClearSelection }
public sealed record ManagerCommand(CommandKind Kind, Guid? WindowId = null,
    Direction? Direction = null, double? Amount = null,
    SplitOrientation? Orientation = null, WindowMode? Mode = null, bool? Paused = null,
    Guid? WorkspaceId = null, Guid? MonitorId = null, string? WorkspaceName = null,
    Guid? NodeId = null, ContainerNavigation? Navigation = null);
public sealed record CommandResult(bool Success, string? ErrorCode, string? Message, long StateRevision);
public sealed record SplitNodeInfo(Guid Id, Guid? WindowId, SplitOrientation? Orientation,
    double Ratio, SplitNodeInfo? First, SplitNodeInfo? Second);
public sealed record ManagerSnapshot(Guid InstanceId, long StateRevision, DesktopSnapshot Desktop,
    IReadOnlyDictionary<Guid, SplitNodeInfo?> Trees, bool Paused,
    IReadOnlyList<WorkspaceInfo>? Workspaces = null, ContainerSelection? Selection = null);
public sealed record ContainerSelection(Guid WorkspaceId, Guid NodeId);

public sealed record WorkspaceDefinition(string Name, string? MonitorDeviceName = null);
public sealed record WorkspaceInfo(Guid Id, string Name, Guid MonitorId, bool Active, IReadOnlyList<Guid> WindowIds);