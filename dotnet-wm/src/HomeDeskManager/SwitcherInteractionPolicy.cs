using Avalonia.Input;

namespace HomeDeskManager;

internal enum WorkspaceContextAction
{
    Activate,
    Settings
}

internal readonly record struct WorkspaceContextTarget(
    WorkspaceContextAction Action,
    Guid WorkspaceId,
    Guid? MonitorId,
    bool IsEnabled);

internal enum SearchEscapeAction
{
    ClearSearch,
    Dismiss
}

internal static class SwitcherInteractionPolicy
{
    public static bool ShouldStartSearch(string? text, KeyModifiers modifiers, bool contextMenuOpen,
        bool searchFocused) =>
        !contextMenuOpen && !searchFocused &&
        (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == KeyModifiers.None &&
        !string.IsNullOrWhiteSpace(text);

    public static bool ShouldShowSearch(string? text) => !string.IsNullOrWhiteSpace(text);

    public static SearchEscapeAction EscapeAction(string? text) =>
        ShouldShowSearch(text) ? SearchEscapeAction.ClearSearch : SearchEscapeAction.Dismiss;

    public static IReadOnlyList<WorkspaceContextTarget> ContextTargets(Guid workspaceId,
        IEnumerable<Guid> monitorIds, bool canActivate)
    {
        ArgumentNullException.ThrowIfNull(monitorIds);
        var targets = monitorIds.Where(monitorId => monitorId != Guid.Empty).Distinct()
            .Select(monitorId => new WorkspaceContextTarget(WorkspaceContextAction.Activate, workspaceId,
                monitorId, canActivate))
            .ToList();
        targets.Add(new WorkspaceContextTarget(WorkspaceContextAction.Settings, workspaceId, null, true));
        return targets;
    }
}