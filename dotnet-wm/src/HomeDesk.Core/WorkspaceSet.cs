using System.Collections.ObjectModel;
using HomeDesk.Contracts;

namespace HomeDesk.Core;

public sealed class WorkspaceSet
{
    private sealed class Workspace(
        string name,
        string? initialDeviceName,
        WorkspaceLayout layout,
        DeskPosition position,
        LayeredLayoutOptions layeredLayoutOptions)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; } = name;
        public string? InitialDeviceName { get; set; } = initialDeviceName;
        public WorkspaceLayout Layout { get; set; } = layout;
        public DeskPosition Position { get; set; } = position;
        public LayeredLayoutOptions LayeredLayoutOptions { get; set; } = layeredLayoutOptions;
    }

    private readonly List<Workspace> workspaces = [];
    private readonly Dictionary<Guid, SplitTree> trees = [];
    private readonly Dictionary<Guid, Guid> assignments = [];
    private readonly Dictionary<Guid, Guid> views = [];
    private Dictionary<Guid, WindowInfo> windows = [];
    private Dictionary<Guid, MonitorInfo> monitors = [];
    private readonly IReadOnlyDictionary<Guid, SplitTree> treeView;

    public WorkspaceSet(IEnumerable<WorkspaceDefinition>? definitions = null)
    {
        treeView = new ReadOnlyDictionary<Guid, SplitTree>(trees);
        if (definitions is null) return;
        var configured = definitions.ToArray();
        if (configured.Any(d => d is null || string.IsNullOrWhiteSpace(d.Name)))
            throw new ArgumentException("Workspace names must not be empty.", nameof(definitions));
        if (configured.Any(d => !Enum.IsDefined(d.Layout)))
            throw new ArgumentException("Workspace layout is not supported.", nameof(definitions));
        if (configured.Any(d => d.Position is { IsValid: false }))
            throw new ArgumentException("Workspace positions must be finite and within the supported desk bounds.",
                nameof(definitions));
        if (configured.Any(d => d.LayeredLayoutOptions is { IsValid: false }))
            throw new ArgumentException("Layered layout options are outside the supported range.", nameof(definitions));
        if (configured.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Length)
            throw new ArgumentException("Workspace names must be unique.", nameof(definitions));
        foreach (var definition in configured)
            Add(definition.Name, definition.MonitorDeviceName, definition.Layout, definition.Position,
                definition.LayeredLayoutOptions);
    }

    public IReadOnlyList<WorkspaceInfo> Snapshot => workspaces.Select(workspace =>
    {
        var monitorId = views.FirstOrDefault(view => view.Value == workspace.Id).Key;
        return new WorkspaceInfo(workspace.Id, workspace.Name, monitorId, monitorId != Guid.Empty,
            Array.AsReadOnly(assignments.Where(assignment => assignment.Value == workspace.Id)
                .Select(assignment => assignment.Key).Order().ToArray()), workspace.Layout, workspace.Position,
            workspace.LayeredLayoutOptions);
    }).ToList().AsReadOnly();

    public IReadOnlyList<WorkspaceViewInfo> Views => views.OrderBy(view => view.Key)
        .Select(view => new WorkspaceViewInfo(view.Key, view.Value)).ToList().AsReadOnly();

    public IReadOnlyDictionary<Guid, SplitTree> Trees => treeView;

    public void Reconcile(DesktopSnapshot desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var ordered = desktop.Monitors.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.Id)
            .ToArray();
        monitors = ordered.ToDictionary(monitor => monitor.Id);
        windows = desktop.Windows.ToDictionary(w => w.Id);
        foreach (var id in assignments.Keys.Where(id => !windows.ContainsKey(id)).ToArray())
            assignments.Remove(id);

        foreach (var monitorId in views.Keys.Where(id => !monitors.ContainsKey(id)).ToArray())
            views.Remove(monitorId);
        foreach (var monitor in ordered)
        {
            if (views.ContainsKey(monitor.Id)) continue;
            var workspace = workspaces.FirstOrDefault(candidate => candidate.InitialDeviceName is not null &&
                                                                   string.Equals(candidate.InitialDeviceName,
                                                                       monitor.DeviceName,
                                                                       StringComparison.OrdinalIgnoreCase) &&
                                                                   !views.Values.Contains(candidate.Id))
                            ?? workspaces.FirstOrDefault(candidate =>
                                candidate.InitialDeviceName is null && !views.Values.Contains(candidate.Id))
                            ?? Add(DefaultName(monitor.DeviceName), null);
            workspace.InitialDeviceName = null;
            views.Add(monitor.Id, workspace.Id);
        }

        foreach (var window in windows.Values.OrderBy(w => w.Id))
        {
            if (assignments.ContainsKey(window.Id)) continue;
            var viewId = views.GetValueOrDefault(window.MonitorId, PrimaryWorkspaceId());
            var target = workspaces.FirstOrDefault(workspace => workspace.Id == viewId);
            target ??= workspaces.FirstOrDefault();
            target ??= Add(DefaultName("Default"), null);
            assignments.Add(window.Id, target.Id);
        }

        ReconcileTrees();
    }

    public bool Activate(Guid workspaceId)
    {
        var targetMonitor = views.FirstOrDefault(view => view.Value == workspaceId).Key;
        if (targetMonitor == Guid.Empty) targetMonitor = PrimaryMonitorId();
        return targetMonitor != Guid.Empty && Activate(workspaceId, targetMonitor);
    }

    public bool Activate(Guid workspaceId, Guid monitorId)
    {
        var target = workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId);
        if (target is null || !monitors.ContainsKey(monitorId) || !views.TryGetValue(monitorId, out var displacedId))
            return false;
        if (displacedId == workspaceId) return false;

        var sourceMonitor = views.FirstOrDefault(view => view.Value == workspaceId).Key;
        if (sourceMonitor != Guid.Empty && sourceMonitor != monitorId)
        {
            var fallback = workspaces.FirstOrDefault(workspace => !views.Values.Contains(workspace.Id) &&
                                                                  workspace.Id != displacedId &&
                                                                  workspace.Id != workspaceId)
                           ?? Add(DefaultName(monitors[sourceMonitor].DeviceName), null);
            fallback.InitialDeviceName = null;
            views[sourceMonitor] = fallback.Id;
        }

        target.InitialDeviceName = null;
        views[monitorId] = target.Id;
        return true;
    }

    public bool MoveWindow(Guid windowId, Guid workspaceId)
    {
        if (!assignments.TryGetValue(windowId, out var current) || current == workspaceId ||
            !workspaces.Any(w => w.Id == workspaceId)) return false;
        assignments[windowId] = workspaceId;
        ReconcileTrees();
        return true;
    }

    public WorkspaceLayout? LayoutFor(Guid workspaceId) =>
        workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId)?.Layout;

    public bool SetLayout(Guid workspaceId, WorkspaceLayout layout, LayeredLayoutOptions? layeredLayoutOptions = null)
    {
        if (!Enum.IsDefined(layout) || layeredLayoutOptions is { IsValid: false }) return false;
        var workspace = workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null) return false;
        workspace.Layout = layout;
        if (layeredLayoutOptions is not null) workspace.LayeredLayoutOptions = layeredLayoutOptions;
        return true;
    }

    public bool SetPosition(Guid workspaceId, DeskPosition position)
    {
        if (!position.IsValid) return false;
        var workspace = workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null) return false;
        workspace.Position = position;
        return true;
    }

    public bool MoveWindowToMonitor(Guid windowId, Guid monitorId)
    {
        return views.TryGetValue(monitorId, out var workspaceId) && MoveWindow(windowId, workspaceId);
    }

    internal State CaptureState() => new(views.ToDictionary(),
        workspaces.ToDictionary(workspace => workspace.Id, workspace => workspace.InitialDeviceName),
        workspaces.ToDictionary(workspace => workspace.Id, workspace => workspace.Position),
        workspaces.Select(workspace => workspace.Id).ToHashSet());

    internal void RestoreState(State state)
    {
        views.Clear();
        foreach (var (monitorId, workspaceId) in state.Views) views.Add(monitorId, workspaceId);
        foreach (var workspace in workspaces.Where(workspace => !state.WorkspaceIds.Contains(workspace.Id)).ToArray())
        {
            workspaces.Remove(workspace);
            trees.Remove(workspace.Id);
            foreach (var windowId in assignments.Where(assignment => assignment.Value == workspace.Id)
                         .Select(assignment => assignment.Key).ToArray()) assignments.Remove(windowId);
        }

        foreach (var workspace in workspaces)
        {
            if (state.InitialDeviceNames.TryGetValue(workspace.Id, out var deviceName))
                workspace.InitialDeviceName = deviceName;
            if (state.Positions.TryGetValue(workspace.Id, out var position))
                workspace.Position = position;
        }
    }

    internal sealed record State(
        Dictionary<Guid, Guid> Views,
        Dictionary<Guid, string?> InitialDeviceNames,
        Dictionary<Guid, DeskPosition> Positions,
        HashSet<Guid> WorkspaceIds);

    public Guid? WorkspaceFor(Guid windowId) => assignments.TryGetValue(windowId, out var id) ? id : null;

    public IReadOnlyList<WindowVisibility> Visibility(DesktopSnapshot desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var active = views.Values.ToHashSet();
        var result = new List<WindowVisibility>();
        foreach (var window in desktop.Windows.OrderBy(w => w.Id))
        {
            if (window.IsIgnored)
            {
                if (window.ManagerHidden && !window.Minimized && !window.Fullscreen)
                    result.Add(new(window.Id, true));
                continue;
            }

            if (window.Minimized || !assignments.TryGetValue(window.Id, out var workspaceId))
                continue;
            if (!active.Contains(workspaceId) && window.Visible)
                result.Add(new(window.Id, false));
            else if (active.Contains(workspaceId) && window.ManagerHidden && !window.Visible)
                result.Add(new(window.Id, true));
        }

        return result.AsReadOnly();
    }

    private Workspace Add(string name, string? deviceName, WorkspaceLayout layout = WorkspaceLayout.Layered,
        DeskPosition? position = null, LayeredLayoutOptions? layeredLayoutOptions = null)
    {
        var workspace = new Workspace(name, deviceName, layout, position ?? DefaultPosition(workspaces.Count),
            layeredLayoutOptions ?? LayeredLayoutOptions.Default);
        workspaces.Add(workspace);
        trees.Add(workspace.Id, new SplitTree());
        return workspace;
    }

    private static DeskPosition DefaultPosition(int index) => new(index % 4 * 480, index / 4 * 360);

    private string DefaultName(string deviceName)
    {
        var name = deviceName;
        for (var suffix = 2;
             workspaces.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
             suffix++)
            name = $"{deviceName} ({suffix})";
        return name;
    }

    private Guid PrimaryMonitorId()
    {
        var primary = monitors.Values.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.Id)
            .FirstOrDefault();
        return primary?.Id ?? Guid.Empty;
    }

    private Guid PrimaryWorkspaceId()
    {
        var monitorId = PrimaryMonitorId();
        return monitorId != Guid.Empty ? views.GetValueOrDefault(monitorId) : Guid.Empty;
    }

    private void ReconcileTrees()
    {
        foreach (var workspace in workspaces)
            trees[workspace.Id].Reconcile(windows.Values
                .Where(w => assignments.GetValueOrDefault(w.Id) == workspace.Id)
                .Select(w => w.ManagerHidden ? w with { Visible = true } : w).ToArray());
    }
}