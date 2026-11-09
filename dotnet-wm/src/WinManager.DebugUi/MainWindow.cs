using System.Text.Json;
using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinManager.Contracts;
using WinManager.Ui;

namespace WinManager.DebugUi;

internal sealed class MainWindow : Window
{
    private sealed record IdentifiedItem(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly bool demoMode;
    private readonly string? pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ListBox monitors = new();
    private readonly ListBox workspaces = new();
    private readonly ListBox windows = new();
    private readonly ListBox exclusions = new();
    private readonly ListBox recentEvents = new();
    private readonly TextBlock connectionText = new();
    private readonly TextBlock statusText = new();
    private readonly TextBlock selectedWindowText = new();
    private readonly TextBlock metricsText = new();
    private readonly TextBlock focusedWorkspaceText = new();
    private readonly TextBlock workspaceSummaryText = new();
    private readonly TextBox positionX = new();
    private readonly TextBox positionY = new();
    private Button connectButton = null!;
    private Button configurationButton = null!;
    private Button positionButton = null!;
    private Button activateButton = null!;
    private Button moveButton = null!;
    private Button focusButton = null!;
    private Button tilingButton = null!;
    private Button floatingButton = null!;
    private Button splitLayoutButton = null!;
    private Button layeredLayoutButton = null!;
    private Button previousPageButton = null!;
    private Button nextPageButton = null!;
    private Button pauseButton = null!;
    private Button redrawButton = null!;
    private readonly List<Button> navigationButtons = [];
    private readonly List<Control> sectionTargets = [];
    private ManagerSession? session;
    private ManagerSnapshot? snapshot;
    private bool connected;
    private bool connecting;
    private bool commandBusy;
    private string? lastMessage;
    private Guid? selectedMonitorId;
    private Guid? selectedWorkspaceId;
    private Guid? selectedWindowId;
    private Guid? positionInputWorkspaceId;
    private DeskPosition? displayedPosition;
    private int selectedSection;
    private ConfigurationEditorWindow? configurationEditor;

    public MainWindow(bool demoMode, string? pipeName = null)
    {
        this.demoMode = demoMode;
        this.pipeName = pipeName;
        Title = "WinManager — Debug console";
        Width = 1240;
        Height = 812;
        MinWidth = 980;
        MinHeight = 700;
        Content = BuildContent();
        Closed += (_, _) => _ = CloseConnectionAsync();
        UpdateActionStates();
        if (demoMode)
        {
            connectionText.Text = "DEMO · commands disabled";
            lastMessage = "Demo data only; no manager connection or desktop actions.";
            ApplySnapshot(DemoSnapshot.Create());
            RecordEvent("snapshot", snapshot!.StateRevision);
        }
        else
        {
            connectionText.Text = "Connecting to the local manager…";
            Opened += (_, _) => _ = ConnectAsync();
        }
    }

    private Control BuildContent()
    {
        Background = DeskTheme.Canvas;
        Foreground = DeskTheme.Text;
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions
                { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(20)
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(0, 0, 0, 14)
        };
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(DeskTheme.Heading("Debug console", 24));
        title.Children.Add(DeskTheme.Caption("ONE DESK  ·  workspaces are window groups, monitors show views"));
        header.Children.Add(title);
        var connection = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        connectionText.Foreground = DeskTheme.Accent;
        connectionText.VerticalAlignment = VerticalAlignment.Center;
        connection.Children.Add(connectionText);
        connectButton = DeskTheme.Action(demoMode ? "Demo mode" : "Connect / reconnect");
        connectButton.Click += async (_, _) => await ConnectAsync();
        connection.Children.Add(connectButton);
        configurationButton = DeskTheme.Action("Edit config");
        configurationButton.Click += async (_, _) => await OpenConfigurationEditorAsync();
        connection.Children.Add(configurationButton);
        Grid.SetColumn(connection, 1);
        header.Children.Add(connection);
        root.Children.Add(DeskTheme.Card(header, new Thickness(18, 14)));

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new(new GridLength(184)), new(new GridLength(1, GridUnitType.Star)), new(new GridLength(320))
            },
            ColumnSpacing = 12
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        WireSelection(monitors);
        WireSelection(workspaces);
        WireSelection(windows);
        ConfigureItems(exclusions);
        ConfigureItems(recentEvents);

        var navigation = new StackPanel { Spacing = 6 };
        navigation.Children.Add(DeskTheme.Caption("INSPECTOR"));
        foreach (var (index, label) in new[]
                 {
                     (0, "Overview"), (1, "Workspaces"), (2, "Windows"), (3, "Monitors"), (4, "Exclusions")
                 })
        {
            var button = DeskTheme.Action(label, index == selectedSection);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) => NavigateTo(index);
            navigationButtons.Add(button);
            navigation.Children.Add(button);
        }

        var pipeInfo = new StackPanel { Spacing = 6, Margin = new Thickness(0, 20, 0, 0) };
        pipeInfo.Children.Add(DeskTheme.Heading("Connection", 14));
        pipeInfo.Children.Add(DeskTheme.Caption(demoMode
            ? "Sample state · actions disabled"
            : "Local manager · commands are never replayed"));
        navigation.Children.Add(pipeInfo);
        Grid.SetColumn(navigation, 0);
        body.Children.Add(DeskTheme.Card(navigation, new Thickness(12)));

        var sections = new StackPanel { Spacing = 12 };
        sectionTargets.Add(BuildOverviewSection());
        sectionTargets.Add(BuildWorkspaceSection());
        sectionTargets.Add(BuildWindowSection());
        sectionTargets.Add(BuildMonitorSection());
        sectionTargets.Add(BuildExclusionSection());
        foreach (var section in sectionTargets) sections.Children.Add(section);
        var center = new ScrollViewer
            { Content = sections, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(center, 1);
        body.Children.Add(center);

        var liveState = new StackPanel { Spacing = 12 };
        var liveHeader = new StackPanel { Spacing = 3 };
        liveHeader.Children.Add(DeskTheme.Heading("Live desk state"));
        liveHeader.Children.Add(DeskTheme.Caption("Shared system state · updates arrive from the manager"));
        liveState.Children.Add(DeskTheme.Card(liveHeader));
        metricsText.TextWrapping = TextWrapping.Wrap;
        liveState.Children.Add(MakePanel("Desk metrics", metricsText));
        focusedWorkspaceText.TextWrapping = TextWrapping.Wrap;
        liveState.Children.Add(MakePanel("Focused workspace", focusedWorkspaceText));
        workspaceSummaryText.TextWrapping = TextWrapping.Wrap;
        liveState.Children.Add(MakePanel("Workspace footprints", workspaceSummaryText));
        recentEvents.Height = 154;
        liveState.Children.Add(MakePanel("Event stream · recent", recentEvents));
        var liveScroll = new ScrollViewer
        {
            Content = liveState, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetColumn(liveScroll, 2);
        body.Children.Add(liveScroll);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            Margin = new Thickness(0, 12, 0, 0)
        };
        statusText.TextWrapping = TextWrapping.Wrap;
        selectedWindowText.TextWrapping = TextWrapping.Wrap;
        selectedWindowText.FontWeight = FontWeight.SemiBold;
        var feedback = new StackPanel { Spacing = 3 };
        feedback.Children.Add(selectedWindowText);
        feedback.Children.Add(statusText);
        footer.Children.Add(feedback);
        var managerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pauseButton = AddAction(managerActions, "Pause manager", async () =>
            await ExecuteAsync(new(CommandKind.Pause, Paused: !(snapshot?.Paused ?? false))));
        redrawButton = AddAction(managerActions, "Rescan / redraw", async () =>
            await ExecuteAsync(new(CommandKind.Redraw)));
        Grid.SetColumn(managerActions, 1);
        footer.Children.Add(managerActions);
        Grid.SetRow(footer, 2);
        root.Children.Add(DeskTheme.Card(footer, new Thickness(14, 10)));
        return root;
    }

    private Control BuildOverviewSection()
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(DeskTheme.Heading("One desk. Every workspace belongs to it.", 19));
        content.Children.Add(DeskTheme.Caption(
            "The manager owns workspace state and window membership. Monitors only display transferable workspace views; visual clients do not create or own desks."));
        var note = new StackPanel { Spacing = 8 };
        note.Children.Add(DeskTheme.Heading("Current system capabilities", 14));
        note.Children.Add(DeskTheme.Caption(
            "Per-workspace layouts: Split and Layered. Layered pages can be navigated with previous / next; overflow remains managed and is reported in window state."));
        note.Children.Add(DeskTheme.Caption(
            "Desk positions are logical coordinates, independent of monitor pixels and DPI. Editing a position does not move or resize native windows."));
        content.Children.Add(MakePanel("System model", note));
        return MakePanel("Overview", content);
    }

    private Control BuildWorkspaceSection()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(DeskTheme.Caption(
            "A workspace is a window group on the shared desk. Its layout and position are workspace-owned."));
        workspaces.Height = 170;
        content.Children.Add(workspaces);
        var layouts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        splitLayoutButton = AddAction(layouts, "Split", async () =>
            await ExecuteAsync(InspectorCommands.SetWorkspaceLayout(SelectedWorkspaceId(), WorkspaceLayout.Split)));
        layeredLayoutButton = AddAction(layouts, "Layered", async () =>
            await ExecuteAsync(InspectorCommands.SetWorkspaceLayout(SelectedWorkspaceId(), WorkspaceLayout.Layered)));
        content.Children.Add(DeskTheme.Caption("Layout rule"));
        content.Children.Add(layouts);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        previousPageButton = AddAction(pages, "Previous page", async () =>
            await ExecuteAsync(
                InspectorCommands.CycleWorkspacePages(SelectedWorkspaceId(), FocusCycleDirection.Previous)));
        nextPageButton = AddAction(pages, "Next page", async () =>
            await ExecuteAsync(InspectorCommands.CycleWorkspacePages(SelectedWorkspaceId(), FocusCycleDirection.Next)));
        content.Children.Add(pages);
        content.Children.Add(DeskTheme.Caption("Logical desk position"));
        var positionFields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        positionX.Width = 100;
        positionX.PlaceholderText = "X";
        positionY.Width = 100;
        positionY.PlaceholderText = "Y";
        positionFields.Children.Add(positionX);
        positionFields.Children.Add(positionY);
        positionButton = AddAction(positionFields, "Save position", ApplyWorkspacePositionAsync, primary: true);
        content.Children.Add(positionFields);
        content.Children.Add(
            DeskTheme.Caption($"Coordinates are bounded to ±{DeskPosition.CoordinateLimit:0} desk units."));
        return MakePanel("Workspace configuration", content);
    }

    private Control BuildWindowSection()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(
            DeskTheme.Caption(
                "Observed windows remain managed even when a native or layout constraint prevents staging."));
        windows.Height = 250;
        content.Children.Add(windows);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        moveButton = AddAction(actions, "Move to workspace", async () =>
            await ExecuteAsync(InspectorCommands.MoveToWorkspace(SelectedWindowId(), SelectedWorkspaceId())));
        focusButton = AddAction(actions, "Focus / promote", async () =>
            await ExecuteAsync(InspectorCommands.Focus(SelectedWindowId())));
        tilingButton = AddAction(actions, "Set tiling", async () =>
            await ExecuteAsync(InspectorCommands.SetMode(SelectedWindowId(), WindowMode.Tiling)));
        floatingButton = AddAction(actions, "Set floating", async () =>
            await ExecuteAsync(InspectorCommands.SetMode(SelectedWindowId(), WindowMode.Floating)));
        content.Children.Add(actions);
        content.Children.Add(DeskTheme.Caption(
            "Mode buttons use the explicitly selected window. Tiling overrides are available for semantic exclusions."));
        return MakePanel("Managed windows", content);
    }

    private Control BuildMonitorSection()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(DeskTheme.Caption(
            "Each connected monitor shows one exclusive workspace view. Activating a workspace elsewhere transfers its view."));
        monitors.Height = 220;
        content.Children.Add(monitors);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        activateButton = AddAction(actions, "Show selected workspace on monitor", async () =>
            await ExecuteAsync(InspectorCommands.Activate(SelectedWorkspaceId(), SelectedMonitorId())), primary: true);
        content.Children.Add(actions);
        return MakePanel("Monitor views", content);
    }

    private Control BuildExclusionSection()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(DeskTheme.Caption(
            "Configured process/class exclusions are read-only here. Change host configuration and restart to include them."));
        exclusions.Height = 230;
        content.Children.Add(exclusions);
        return MakePanel("Configured exclusions", content);
    }

    private Button AddAction(Panel parent, string label, Func<Task> action, bool primary = false)
    {
        var button = DeskTheme.Action(label, primary);
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception error)
            {
                lastMessage = $"Command was not sent: {error.Message}";
                UpdateActionStates();
            }
        };
        parent.Children.Add(button);
        return button;
    }

    private void NavigateTo(int section)
    {
        selectedSection = section;
        for (var index = 0; index < navigationButtons.Count; index++)
        {
            var selected = index == section;
            navigationButtons[index].Background = selected ? DeskTheme.Accent : DeskTheme.Surface;
            navigationButtons[index].Foreground = selected ? DeskTheme.Surface : DeskTheme.Text;
        }

        if (sectionTargets.Count > section) sectionTargets[section].BringIntoView();
    }

    private static Border MakePanel(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(DeskTheme.Heading(title, 15));
        stack.Children.Add(content);
        return DeskTheme.Card(stack, new Thickness(14));
    }

    private static void ConfigureItems(ListBox list) => list.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
        new TextBlock { Text = item?.ToString() ?? "", TextWrapping = TextWrapping.Wrap });

    private void WireSelection(ListBox list)
    {
        ConfigureItems(list);
        list.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(list, monitors)) selectedMonitorId = SelectedMonitorId();
            if (ReferenceEquals(list, workspaces))
            {
                selectedWorkspaceId = SelectedWorkspaceId();
                RefreshPositionEditor();
            }

            if (ReferenceEquals(list, windows)) selectedWindowId = SelectedWindowId();
            UpdateActionStates();
        };
    }

    private void ApplySnapshot(ManagerSnapshot next)
    {
        snapshot = next;
        var viewMap = InspectorPresentation.ViewMap(next);
        selectedMonitorId = Populate(monitors,
            next.Desktop.Monitors.Select(monitor =>
            {
                var workspaceId = viewMap.GetValueOrDefault(monitor.Id);
                var workspace = next.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
                var windowCount = workspace?.WindowIds.Count ?? 0;
                return new IdentifiedItem(monitor.Id,
                    $"{monitor.DeviceName} · {monitor.Bounds.Width}×{monitor.Bounds.Height} · {workspace?.Name ?? "NO VIEW"} · {windowCount} windows");
            }), selectedMonitorId);
        selectedWorkspaceId = Populate(workspaces,
            (next.Workspaces ?? []).Select(workspace =>
            {
                var monitorId = viewMap.FirstOrDefault(view => view.Value == workspace.Id).Key;
                var monitor = next.Desktop.Monitors.FirstOrDefault(candidate => candidate.Id == monitorId);
                var view = monitorId != Guid.Empty ? $"view on {monitor?.DeviceName ?? "monitor"}" : "unviewed";
                var position = workspace.Position is { } point
                    ? $"desk ({point.X.ToString("0.##", CultureInfo.InvariantCulture)}, {point.Y.ToString("0.##", CultureInfo.InvariantCulture)})"
                    : "desk position unavailable";
                return new IdentifiedItem(workspace.Id,
                    $"{workspace.Name} · {workspace.Layout} · {view} · {position} · {workspace.WindowIds.Count} windows");
            }), selectedWorkspaceId);
        selectedWindowId = Populate(windows, next.Desktop.Windows.Select(window =>
        {
            var monitorName =
                next.Desktop.Monitors.FirstOrDefault(monitor => monitor.Id == window.MonitorId)?.DeviceName ??
                "unknown monitor";
            var workspace = next.Workspaces?.FirstOrDefault(candidate => candidate.WindowIds.Contains(window.Id));
            var state = InspectorPresentation.WindowState(window);
            return new IdentifiedItem(window.Id,
                $"{window.Title}\n{window.ProcessName} / {window.ClassName} · {window.Mode} · {state}\nPhysical: {monitorName} · Workspace: {workspace?.Name ?? "unassigned"} · {window.Bounds.Width}×{window.Bounds.Height} · minimum {window.MinimumWidth}×{window.MinimumHeight}");
        }), selectedWindowId);

        exclusions.Items.Clear();
        if (next.Desktop.ConfiguredExclusions is null)
            exclusions.Items.Add(
                "Unavailable: this manager version does not publish configured-exclusion diagnostics.");
        else if (next.Desktop.ConfiguredExclusions.Count == 0)
            exclusions.Items.Add("No windows matched configured process/class exclusions.");
        else
            foreach (var excluded in next.Desktop.ConfiguredExclusions)
            {
                var monitor =
                    next.Desktop.Monitors.FirstOrDefault(candidate => candidate.Id == excluded.MonitorId)?.DeviceName ??
                    "unknown monitor";
                var reasons = new List<string>();
                if (excluded.ProcessNameExcluded) reasons.Add("configured process name");
                if (excluded.ClassNameExcluded) reasons.Add("configured class name");
                exclusions.Items.Add(
                    $"{excluded.Title} · {excluded.ProcessName} / {excluded.ClassName} · {string.Join(" + ", reasons)} · {monitor}");
            }

        var activeWorkspaces = next.Workspaces?.Count(workspace => workspace.Active) ?? 0;
        var overflow = next.Desktop.Windows.Count(window => window.LayoutOverflow);
        metricsText.Text =
            $"{next.Desktop.Monitors.Count} monitors\n{next.Workspaces?.Count ?? 0} workspaces · {activeWorkspaces} viewed\n{next.Desktop.Windows.Count} managed windows · {overflow} overflow\nRevision {next.StateRevision}";
        var focused = next.Desktop.Windows.FirstOrDefault(window => window.Id == next.Desktop.FocusedWindowId);
        var focusedWorkspace = focused is null
            ? null
            : next.Workspaces?.FirstOrDefault(workspace => workspace.WindowIds.Contains(focused.Id));
        focusedWorkspaceText.Text = focused is null || focusedWorkspace is null
            ? "No focused window is assigned to a workspace."
            : $"{focusedWorkspace.Name}\n{focused.Title}\n{focused.Mode} · {InspectorPresentation.WindowState(focused)}";
        workspaceSummaryText.Text = string.Join(Environment.NewLine, (next.Workspaces ?? []).Select(workspace =>
        {
            var position = workspace.Position is { } point
                ? $"({point.X.ToString("0.#", CultureInfo.InvariantCulture)}, {point.Y.ToString("0.#", CultureInfo.InvariantCulture)})"
                : "unavailable";
            return $"{workspace.Name}  ·  {position}  ·  {workspace.WindowIds.Count} windows";
        }));
        if (connected) connectionText.Text = $"Connected · revision {next.StateRevision}";
        RefreshPositionEditor();
        UpdateActionStates();
    }


    private static Guid? Populate(ListBox list, IEnumerable<IdentifiedItem> items, Guid? selectedId)
    {
        var materialized = items.ToArray();
        list.Items.Clear();
        foreach (var item in materialized) list.Items.Add(item);
        var selected = materialized.FirstOrDefault(item => item.Id == selectedId) ?? materialized.FirstOrDefault();
        list.SelectedItem = selected;
        return selected?.Id;
    }


    private Guid? SelectedMonitorId() => (monitors.SelectedItem as IdentifiedItem)?.Id;
    private Guid? SelectedWorkspaceId() => (workspaces.SelectedItem as IdentifiedItem)?.Id;
    private Guid? SelectedWindowId() => (windows.SelectedItem as IdentifiedItem)?.Id;

    private void RefreshPositionEditor()
    {
        var workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == SelectedWorkspaceId());
        if (workspace is null)
        {
            positionInputWorkspaceId = null;
            displayedPosition = null;
            positionX.Text = "";
            positionY.Text = "";
            return;
        }

        if (positionInputWorkspaceId != workspace.Id || displayedPosition != workspace.Position)
        {
            positionX.Text = workspace.Position?.X.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
            positionY.Text = workspace.Position?.Y.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
            displayedPosition = workspace.Position;
        }

        positionInputWorkspaceId = workspace.Id;
    }

    private async Task ApplyWorkspacePositionAsync()
    {
        var workspaceId = SelectedWorkspaceId();
        if (workspaceId is null) return;
        if (!double.TryParse(positionX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(positionY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            lastMessage = "Position was not sent: enter finite numeric X and Y coordinates.";
            UpdateActionStates();
            return;
        }

        var position = new DeskPosition(x, y);
        if (!position.IsValid)
        {
            lastMessage =
                $"Position was not sent: coordinates must be finite and within ±{DeskPosition.CoordinateLimit:0}.";
            UpdateActionStates();
            return;
        }

        await ExecuteAsync(new(CommandKind.SetWorkspacePosition, WorkspaceId: workspaceId, Position: position));
    }

    private void RecordEvent(string eventType, long revision)
    {
        recentEvents.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  ·  {eventType}  ·  revision {revision}");
        while (recentEvents.Items.Count > 10) recentEvents.Items.RemoveAt(recentEvents.Items.Count - 1);
    }

    private void UpdateActionStates()
    {
        var canCommand = connected && snapshot is not null && !demoMode && !connecting && !commandBusy;
        connectButton.IsEnabled = !demoMode && !connecting && !connected && !commandBusy;
        configurationButton.IsEnabled = connected && !demoMode && !connecting && !commandBusy;
        activateButton.IsEnabled = canCommand && SelectedWorkspaceId() is not null && SelectedMonitorId() is not null;
        moveButton.IsEnabled = canCommand && SelectedWindowId() is not null && SelectedWorkspaceId() is not null;
        focusButton.IsEnabled = canCommand && SelectedWindowId() is not null;
        var selectedWindow = snapshot?.Desktop.Windows.FirstOrDefault(window => window.Id == SelectedWindowId());
        selectedWindowText.Text = InspectorPresentation.SelectedWindow(selectedWindow);
        tilingButton.IsEnabled = InspectorPresentation.CanSetMode(selectedWindow, WindowMode.Tiling, canCommand);
        floatingButton.IsEnabled = InspectorPresentation.CanSetMode(selectedWindow, WindowMode.Floating, canCommand);
        var selectedWorkspace =
            snapshot?.Workspaces?.FirstOrDefault(workspace => workspace.Id == SelectedWorkspaceId());
        RefreshPositionEditor();
        positionButton.IsEnabled = canCommand && selectedWorkspace?.Position is not null;
        positionX.IsEnabled = canCommand && selectedWorkspace?.Position is not null;
        positionY.IsEnabled = canCommand && selectedWorkspace?.Position is not null;
        splitLayoutButton.IsEnabled = canCommand && selectedWorkspace is not null &&
                                      selectedWorkspace.Layout != WorkspaceLayout.Split;
        layeredLayoutButton.IsEnabled = canCommand && selectedWorkspace is not null &&
                                        selectedWorkspace.Layout != WorkspaceLayout.Layered;
        previousPageButton.IsEnabled =
            InspectorPresentation.CanCyclePages(selectedWorkspace, snapshot?.Desktop, canCommand);
        nextPageButton.IsEnabled = previousPageButton.IsEnabled;
        pauseButton.IsEnabled = canCommand;
        redrawButton.IsEnabled = canCommand;
        pauseButton.Content = snapshot?.Paused == true ? "Resume manager" : "Pause manager";
        connectionText.Foreground = connected || demoMode ? DeskTheme.Accent : DeskTheme.Error;
        statusText.Text = lastMessage ?? (demoMode
            ? "Demo mode: sample state only; actions are disabled."
            : connected
                ? "Live snapshot updates are subscribed. Commands are sent once; reconnect and inspect state if a connection is lost."
                : connecting
                    ? "Connecting…"
                    : "Disconnected. Use Connect / reconnect to request a fresh snapshot.");
        if (statusText.Text?.Length > 1200) statusText.Text = statusText.Text[..1200] + "…";
    }

    private async Task ConnectAsync()
    {
        if (demoMode || connecting || connected) return;
        connecting = true;
        lastMessage = null;
        connectionText.Text = "Connecting to the local manager…";
        UpdateActionStates();
        ManagerSession? nextSession = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            nextSession = await ManagerSession.ConnectAsync(pipeName, deadline.Token, eventQueueCapacity: 64);
            var connectedSession = nextSession;
            session = connectedSession;
            nextSession = null;
            connected = true;
            connectionText.Text = "Connected · waiting for snapshot";
            UpdateActionStates();
            connectedSession.Start((next, eventType) => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(session, connectedSession)) return;
                    ApplySnapshot(next);
                    RecordEvent(eventType, next.StateRevision);
                }),
                failure => Dispatcher.UIThread.Post(() => HandleDisconnect(connectedSession, failure)), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            lastMessage = $"Connect failed: {error.Message}";
            connectionText.Text = snapshot is null ? "Disconnected" : "Disconnected · snapshot is stale";
        }
        finally
        {
            if (nextSession is not null) await nextSession.DisposeAsync();
            connecting = false;
            UpdateActionStates();
        }
    }

    private async Task OpenConfigurationEditorAsync()
    {
        if (demoMode || !connected || session is not { } activeSession || commandBusy) return;
        if (configurationEditor is { } existing)
        {
            existing.Activate();
            return;
        }

        commandBusy = true;
        lastMessage = "Reading the host configuration…";
        UpdateActionStates();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var document = await activeSession.ReadConfigurationAsync(deadline.Token);
            if (!ReferenceEquals(session, activeSession)) return;
            var window = new ConfigurationEditorWindow(activeSession, document, () => snapshot);
            configurationEditor = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(configurationEditor, window)) configurationEditor = null;
            };
            window.Show(this);
            lastMessage = "Configuration editor opened. Saving requires a manager restart to take effect.";
        }
        catch (Exception error)
        {
            lastMessage = $"Configuration could not be opened: {error.Message}";
        }
        finally
        {
            commandBusy = false;
            UpdateActionStates();
        }
    }

    private void HandleDisconnect(ManagerSession source, Exception? failure)
    {
        if (!ReferenceEquals(session, source)) return;
        session = null;
        connected = false;
        connectionText.Text = "Disconnected · snapshot is stale";
        var disconnectMessage = failure is null
            ? "Connection closed. Reconnect for a fresh snapshot; no missed commands are replayed."
            : $"Connection lost: {failure}. Command outcome may be unknown; reconnect and inspect state.";
        lastMessage = lastMessage is null ? disconnectMessage : $"{lastMessage}\n{disconnectMessage}";
        UpdateActionStates();
        _ = source.DisposeAsync();
    }

    private async Task ExecuteAsync(ManagerCommand command)
    {
        var activeSession = session;
        if (!connected || activeSession is null || commandBusy) return;
        var target = snapshot?.Desktop.Windows.FirstOrDefault(window => window.Id == command.WindowId);
        var description = InspectorPresentation.CommandDescription(command, target);
        commandBusy = true;
        lastMessage = InspectorPresentation.CommandFeedback(description, "Sending command…");
        UpdateActionStates();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await activeSession.ExecuteAsync(command, deadline.Token);
            lastMessage = InspectorPresentation.CommandFeedback(description, result.Success
                ? $"Command completed · revision {result.StateRevision}"
                : $"Command failed · {result.ErrorCode}: {result.Message}");
        }
        catch (Exception error)
        {
            lastMessage = InspectorPresentation.CommandFeedback(description, activeSession.IsConnected
                ? $"Command failed · {error.Message}"
                : $"Connection lost during command · outcome may be unknown. Reconnect and inspect state. {error.Message}");
        }
        finally
        {
            commandBusy = false;
            UpdateActionStates();
        }
    }

    private async Task CloseConnectionAsync()
    {
        await lifetime.CancelAsync();
        if (session is { } current) await current.DisposeAsync();
    }
}