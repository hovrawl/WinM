namespace WinManager.Contracts;

public static class ManagerEventTypes
{
    public const string Snapshot = "snapshot";
    public const string WindowManaged = "window.managed";
    public const string WindowUnmanaged = "window.unmanaged";
    public const string WindowUpdated = "window.updated";
    public const string FocusChanged = "focus.changed";
    public const string MonitorAdded = "monitor.added";
    public const string MonitorRemoved = "monitor.removed";
    public const string MonitorUpdated = "monitor.updated";
    public const string WorkspaceActivated = "workspace.activated";
    public const string WorkspaceDeactivated = "workspace.deactivated";
    public const string WorkspaceUpdated = "workspace.updated";
    public const string LayoutChanged = "layout.changed";
    public const string PauseChanged = "pause.changed";
    public const string ApplicationExiting = "application.exiting";
}