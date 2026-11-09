using System.Text.Json.Serialization;

namespace WinManager.Contracts;

public enum Direction
{
    Left,
    Right,
    Up,
    Down
}

public enum SplitOrientation
{
    Horizontal,
    Vertical
}

public enum ContainerNavigation
{
    Parent,
    FirstChild,
    SecondChild,
    Sibling,
    Root
}

public enum FocusCycleDirection
{
    Next,
    Previous
}

public enum WorkspaceLayout
{
    Split,
    Layered
}

public sealed record LayeredLayoutOptions(double WindowWidthRatio = 1, int HorizontalOffset = 32)
{
    public static LayeredLayoutOptions Default { get; } = new();

    [JsonIgnore]
    public bool IsValid => double.IsFinite(WindowWidthRatio) && WindowWidthRatio > 0 && WindowWidthRatio <= 1 &&
                           HorizontalOffset is >= 0 and <= 256;
}

public enum CommandKind
{
    Focus,
    Move,
    Resize,
    SetSplit,
    SetMode,
    Pause,
    Redraw,
    Exit,
    ActivateWorkspace,
    MoveToWorkspace,
    MoveToMonitor,
    ToggleMode,
    SelectContainer,
    ClearSelection,
    FocusCycle,
    SetWorkspaceLayout,
    SetWorkspacePosition,
    FocusMonitor,
    MoveSpatial
}

public sealed record DeskPosition(double X, double Y)
{
    public const double CoordinateLimit = 100_000;

    [JsonIgnore]
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
                           Math.Abs(X) <= CoordinateLimit && Math.Abs(Y) <= CoordinateLimit;
}

public sealed record ManagerCommand(
    CommandKind Kind,
    Guid? WindowId = null,
    Direction? Direction = null,
    double? Amount = null,
    SplitOrientation? Orientation = null,
    WindowMode? Mode = null,
    bool? Paused = null,
    Guid? WorkspaceId = null,
    Guid? MonitorId = null,
    string? WorkspaceName = null,
    Guid? NodeId = null,
    ContainerNavigation? Navigation = null,
    FocusCycleDirection? CycleDirection = null,
    WorkspaceLayout? Layout = null,
    DeskPosition? Position = null,
    LayeredLayoutOptions? LayeredLayoutOptions = null,
    int? MonitorNumber = null,
    int? WorkspaceNumber = null);

public sealed record CommandResult(bool Success, string? ErrorCode, string? Message, long StateRevision);

public sealed record SplitNodeInfo(
    Guid Id,
    Guid? WindowId,
    SplitOrientation? Orientation,
    double Ratio,
    SplitNodeInfo? First,
    SplitNodeInfo? Second);

public sealed record ManagerSnapshot(
    Guid InstanceId,
    long StateRevision,
    DesktopSnapshot Desktop,
    IReadOnlyDictionary<Guid, SplitNodeInfo?> Trees,
    bool Paused,
    IReadOnlyList<WorkspaceInfo>? Workspaces = null,
    ContainerSelection? Selection = null,
    IReadOnlyList<WorkspaceViewInfo>? WorkspaceViews = null);

public sealed record ContainerSelection(Guid WorkspaceId, Guid NodeId);

public sealed record WorkspaceDefinition(
    string Name,
    string? MonitorDeviceName = null,
    WorkspaceLayout Layout = WorkspaceLayout.Layered,
    DeskPosition? Position = null,
    LayeredLayoutOptions? LayeredLayoutOptions = null);

public sealed record WorkspaceInfo(
    Guid Id,
    string Name,
    Guid MonitorId,
    bool Active,
    IReadOnlyList<Guid> WindowIds,
    WorkspaceLayout Layout = WorkspaceLayout.Layered,
    DeskPosition? Position = null,
    LayeredLayoutOptions? LayeredLayoutOptions = null);

public sealed record WorkspaceViewInfo(Guid MonitorId, Guid WorkspaceId);