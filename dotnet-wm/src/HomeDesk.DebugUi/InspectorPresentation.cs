using HomeDesk.Contracts;

namespace HomeDesk.DebugUi;

internal static class InspectorPresentation
{
    public static bool CanSetMode(WindowInfo? window, WindowMode mode, bool canCommand) =>
        canCommand && window is not null &&
        (window.Mode != mode || mode == WindowMode.Tiling && window.IsIgnored);

    public static string SelectedWindow(WindowInfo? window) => window is null
        ? "No window selected. Select a window to change its mode."
        : $"Selected window: {WindowTarget(window)} · Current mode: {window.Mode} · {WindowState(window)}";

    public static string CommandDescription(ManagerCommand command, WindowInfo? window)
    {
        var action = command.Kind == CommandKind.SetMode ? $"Set {command.Mode}" : command.Kind.ToString();
        if (command.WindowId is not { } id) return action;
        var target = window?.Id == id ? WindowTarget(window) : $"window [{id}]";
        return $"{action} → {target}";
    }

    public static string CommandFeedback(string description, string outcome) => $"{description} · {outcome}";

    public static bool CanCyclePages(WorkspaceInfo? workspace, DesktopSnapshot? desktop, bool canCommand) =>
        canCommand && workspace is { Active: true, Layout: WorkspaceLayout.Layered } && desktop is not null &&
        desktop.Windows.Count(window => workspace.WindowIds.Contains(window.Id) && window.Mode == WindowMode.Tiling &&
                                        window.Visible && !window.Minimized && !window.IsIgnored &&
                                        window.Bounds.Width > 0 && window.Bounds.Height > 0) > 1;

    private static string WindowTarget(WindowInfo window)
    {
        var title = string.IsNullOrWhiteSpace(window.Title) ? "Untitled window" : window.Title;
        if (title.Length > 160) title = title[..160] + "…";
        return $"“{title}” [{window.Id}]";
    }

    public static Dictionary<Guid, Guid> ViewMap(ManagerSnapshot current)
    {
        if (current.WorkspaceViews is not null)
            return current.WorkspaceViews.ToDictionary(view => view.MonitorId, view => view.WorkspaceId);
        return (current.Workspaces ?? []).Where(workspace => workspace.Active && workspace.MonitorId != Guid.Empty)
            .ToDictionary(workspace => workspace.MonitorId, workspace => workspace.Id);
    }

    public static string WindowState(WindowInfo window)
    {
        List<string> state = [];
        state.Add(window.Visible ? "visible" : "hidden");
        if (window.Minimized) state.Add("minimized");
        if (window.ManagerHidden) state.Add("manager-hidden");
        if (window.LayoutOverflow) state.Add("LAYOUT OVERFLOW: minimum size does not fit");
        var flags = new List<string>();
        if (window.Fullscreen) flags.Add("fullscreen");
        if (window.IsPopup) flags.Add("popup");
        if (window.IsToolWindow) flags.Add("tool");
        if (window.IsOwned) flags.Add("owned");
        if (window.IsNoActivate) flags.Add("no-activate");
        if (flags.Count > 0)
            state.Add((window.IsIgnored ? "default ignored: " : "native flags: ") + string.Join(", ", flags));
        if (window.TilingOverride is { } tiling) state.Add(tiling ? "tiling override" : "floating override");
        return string.Join(" · ", state);
    }
}