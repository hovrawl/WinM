using HomeDesk.Contracts;

namespace HomeDesk.DebugUi;

internal static class InspectorCommands
{
    public static ManagerCommand Activate(Guid? workspaceId, Guid? monitorId) =>
        new(CommandKind.ActivateWorkspace, WorkspaceId: Required(workspaceId), MonitorId: Required(monitorId));

    public static ManagerCommand MoveToWorkspace(Guid? windowId, Guid? workspaceId) =>
        new(CommandKind.MoveToWorkspace, WindowId: Required(windowId), WorkspaceId: Required(workspaceId));

    public static ManagerCommand Focus(Guid? windowId) => new(CommandKind.Focus, WindowId: Required(windowId));

    public static ManagerCommand SetMode(Guid? windowId, WindowMode mode) =>
        new(CommandKind.SetMode, WindowId: Required(windowId), Mode: mode);

    public static ManagerCommand SetWorkspaceLayout(Guid? workspaceId, WorkspaceLayout layout) =>
        new(CommandKind.SetWorkspaceLayout, WorkspaceId: Required(workspaceId), Layout: layout);

    public static ManagerCommand CycleWorkspacePages(Guid? workspaceId, FocusCycleDirection direction) =>
        new(CommandKind.FocusCycle, WorkspaceId: Required(workspaceId), Mode: WindowMode.Tiling,
            CycleDirection: direction);

    private static Guid Required(Guid? id) => id is { } value && value != Guid.Empty
        ? value
        : throw new InvalidOperationException("Select an explicit target before sending a command.");
}