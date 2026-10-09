using System.Collections.ObjectModel;
using WinManager.Contracts;

namespace WinManager.Core;

public sealed class WorkspaceSet
{
    private sealed class Workspace(string name, string? deviceName, Guid monitorId)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; } = name;
        public string? DeviceName { get; set; } = deviceName;
        public Guid MonitorId { get; set; } = monitorId;
        public bool Active { get; set; }
    }

    private readonly List<Workspace> workspaces = [];
    private readonly Dictionary<Guid, SplitTree> trees = [];
    private readonly Dictionary<Guid, Guid> assignments = [];
    private Dictionary<Guid, WindowInfo> windows = [];
    private HashSet<Guid> monitors = [];
    private readonly IReadOnlyDictionary<Guid, SplitTree> treeView;

    public WorkspaceSet(IEnumerable<WorkspaceDefinition>? definitions = null)
    {
        treeView = new ReadOnlyDictionary<Guid, SplitTree>(trees);
        if (definitions is null) return;
        var configured = definitions.ToArray();
        if (configured.Any(d => d is null || string.IsNullOrWhiteSpace(d.Name)))
            throw new ArgumentException("Workspace names must not be empty.", nameof(definitions));
        if (configured.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Length)
            throw new ArgumentException("Workspace names must be unique.", nameof(definitions));
        foreach (var definition in configured)
            Add(definition.Name, definition.MonitorDeviceName, Guid.Empty);
    }

    public IReadOnlyList<WorkspaceInfo> Snapshot => workspaces.Select(w => new WorkspaceInfo(
        w.Id, w.Name, w.MonitorId, w.Active,
        Array.AsReadOnly(assignments.Where(a => a.Value == w.Id).Select(a => a.Key).Order().ToArray())))
        .ToList().AsReadOnly();

    public IReadOnlyDictionary<Guid, SplitTree> Trees => treeView;

    public void Reconcile(DesktopSnapshot desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var ordered = desktop.Monitors.OrderBy(m => m.Id).ToArray();
        monitors = ordered.Select(m => m.Id).ToHashSet();
        windows = desktop.Windows.ToDictionary(w => w.Id);
        foreach (var id in assignments.Keys.Where(id => !windows.ContainsKey(id)).ToArray())
            assignments.Remove(id);

        if (ordered.Length > 0)
        {
            // Windows places the primary display at the virtual desktop origin.
            var initialMonitor = ordered.FirstOrDefault(m => m.Bounds.X == 0 && m.Bounds.Y == 0)
                ?? ordered.OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id).First();
            foreach (var workspace in workspaces)
            {
                var preferred = ordered.FirstOrDefault(m => workspace.DeviceName is not null &&
                    string.Equals(m.DeviceName, workspace.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (preferred is not null) workspace.MonitorId = preferred.Id;
                else if (workspace.MonitorId == Guid.Empty) workspace.MonitorId = initialMonitor.Id;
                else if (!monitors.Contains(workspace.MonitorId)) workspace.MonitorId = ordered[0].Id;
                workspace.DeviceName ??= ordered.First(m => m.Id == workspace.MonitorId).DeviceName;
            }
            foreach (var monitor in ordered)
                if (!workspaces.Any(w => w.MonitorId == monitor.Id))
                    Add(DefaultName(monitor.DeviceName), monitor.DeviceName, monitor.Id);
        }

        NormalizeActive();
        foreach (var window in windows.Values.OrderBy(w => w.Id))
        {
            if (assignments.ContainsKey(window.Id)) continue;
            var target = workspaces.FirstOrDefault(w => w.MonitorId == window.MonitorId && w.Active)
                ?? workspaces.FirstOrDefault(w => w.MonitorId == window.MonitorId);
            if (target is null && ordered.Length > 0)
                target = workspaces.FirstOrDefault(w => w.MonitorId == ordered[0].Id && w.Active)
                    ?? workspaces.First(w => w.MonitorId == ordered[0].Id);
            target ??= workspaces.FirstOrDefault();
            target ??= Add(DefaultName("Default"), null, window.MonitorId);
            assignments.Add(window.Id, target.Id);
        }
        NormalizeActive();
        ReconcileTrees();
    }

    public bool Activate(Guid workspaceId)
    {
        var target = workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (target is null || target.Active) return false;
        foreach (var workspace in workspaces.Where(w => w.MonitorId == target.MonitorId))
            workspace.Active = workspace == target;
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

    public bool MoveWindowToMonitor(Guid windowId, Guid monitorId)
    {
        if (!monitors.Contains(monitorId)) return false;
        var target = workspaces.FirstOrDefault(w => w.MonitorId == monitorId && w.Active);
        return target is not null && MoveWindow(windowId, target.Id);
    }

    public Guid? WorkspaceFor(Guid windowId) => assignments.TryGetValue(windowId, out var id) ? id : null;

    public IReadOnlyList<WindowVisibility> Visibility(DesktopSnapshot desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var active = workspaces.Where(w => w.Active).Select(w => w.Id).ToHashSet();
        var result = new List<WindowVisibility>();
        foreach (var window in desktop.Windows.OrderBy(w => w.Id))
        {
            if (window.Minimized || window.Fullscreen || !assignments.TryGetValue(window.Id, out var workspaceId))
                continue;
            if (!active.Contains(workspaceId) && window.Visible)
                result.Add(new(window.Id, false));
            else if (active.Contains(workspaceId) && window.ManagerHidden && !window.Visible)
                result.Add(new(window.Id, true));
        }
        return result.AsReadOnly();
    }

    private Workspace Add(string name, string? deviceName, Guid monitorId)
    {
        var workspace = new Workspace(name, deviceName, monitorId);
        workspaces.Add(workspace);
        trees.Add(workspace.Id, new SplitTree());
        return workspace;
    }

    private string DefaultName(string deviceName)
    {
        var name = deviceName;
        for (var suffix = 2; workspaces.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{deviceName} ({suffix})";
        return name;
    }

    private void NormalizeActive()
    {
        foreach (var group in workspaces.GroupBy(w => w.MonitorId))
        {
            var active = group.FirstOrDefault(w => w.Active) ?? group.First();
            foreach (var workspace in group) workspace.Active = workspace == active;
        }
    }

    private void ReconcileTrees()
    {
        foreach (var workspace in workspaces)
            trees[workspace.Id].Reconcile(windows.Values
                .Where(w => assignments.GetValueOrDefault(w.Id) == workspace.Id)
                .Select(w => w.ManagerHidden ? w with { Visible = true } : w).ToArray());
    }
}