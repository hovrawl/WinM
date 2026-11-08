using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinManager.Client;
using WinManager.Contracts;

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
    private readonly TextBlock connectionText = new();
    private readonly TextBlock statusText = new();
    private readonly TextBlock selectedWindowText = new();
    private Button connectButton = null!;
    private Button activateButton = null!;
    private Button moveButton = null!;
    private Button focusButton = null!;
    private Button tilingButton = null!;
    private Button floatingButton = null!;
    private Button pauseButton = null!;
    private Button redrawButton = null!;
    private WinManagerClient? client;
    private ManagerSnapshot? snapshot;
    private bool connected;
    private bool connecting;
    private bool commandBusy;
    private string? lastMessage;
    private Guid? selectedMonitorId;
    private Guid? selectedWorkspaceId;
    private Guid? selectedWindowId;

    public MainWindow(bool demoMode, string? pipeName = null)
    {
        this.demoMode = demoMode;
        this.pipeName = pipeName;
        Title = "WinManager — One shared desk";
        Width = 1280;
        Height = 820;
        MinWidth = 960;
        MinHeight = 640;
        Content = BuildContent();
        Closed += (_, _) => _ = CloseConnectionAsync();
        UpdateActionStates();
        if (demoMode)
        {
            connectionText.Text = "DEMO · commands disabled";
            lastMessage = "Demo data only; no manager connection or desktop actions.";
            ApplySnapshot(DemoSnapshot.Create());
        }
        else
        {
            connectionText.Text = "Connecting to the local manager…";
            Opened += (_, _) => _ = ConnectAsync();
        }
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)),
                new(new GridLength(190))
            }
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock
        {
            Text = "One shared desk", FontSize = 25, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        connectionText.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(connectionText);
        connectButton = new Button { Content = demoMode ? "Demo mode" : "Connect / reconnect", MinWidth = 150 };
        connectButton.Click += async (_, _) => await ConnectAsync();
        header.Children.Add(connectButton);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var explanation = new TextBlock
        {
            Text = "We manage one desk. Workspaces are window groups on that desk; monitors only show a workspace view.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(explanation, 1);
        root.Children.Add(explanation);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 6) };
        activateButton = AddAction(actions, "Show workspace on monitor", async () =>
            await ExecuteAsync(InspectorCommands.Activate(SelectedWorkspaceId(), SelectedMonitorId())));
        moveButton = AddAction(actions, "Move window to workspace", async () =>
            await ExecuteAsync(InspectorCommands.MoveToWorkspace(SelectedWindowId(), SelectedWorkspaceId())));
        focusButton = AddAction(actions, "Focus / promote", async () =>
            await ExecuteAsync(InspectorCommands.Focus(SelectedWindowId())));
        tilingButton = AddAction(actions, "Set tiling", async () =>
            await ExecuteAsync(InspectorCommands.SetMode(SelectedWindowId(), WindowMode.Tiling)));
        floatingButton = AddAction(actions, "Set floating", async () =>
            await ExecuteAsync(InspectorCommands.SetMode(SelectedWindowId(), WindowMode.Floating)));
        pauseButton = AddAction(actions, "Pause manager", async () =>
            await ExecuteAsync(new(CommandKind.Pause, Paused: !(snapshot?.Paused ?? false))));
        redrawButton = AddAction(actions, "Rescan / redraw", async () =>
            await ExecuteAsync(new(CommandKind.Redraw)));
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        var lists = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new(new GridLength(1, GridUnitType.Star)), new(new GridLength(1, GridUnitType.Star)),
                new(new GridLength(2, GridUnitType.Star))
            },
            ColumnSpacing = 12
        };
        WireSelection(monitors);
        WireSelection(workspaces);
        WireSelection(windows);
        AddPanel(lists, "Monitor views onto this desk", monitors, 0, 0);
        AddPanel(lists, "Workspaces · window groups, not desks", workspaces, 1, 0);
        AddPanel(lists, "Windows · physical monitor vs workspace membership", windows, 2, 0);
        var desk = MakePanel("The desk · all managed workspaces and windows", lists);
        Grid.SetRow(desk, 3);
        root.Children.Add(desk);

        exclusions.MinHeight = 110;
        var exclusionsPanel = MakePanel("Configured exclusions · read-only; edit ignored process/class configuration and restart to include", exclusions);
        ConfigureItems(exclusions);
        Grid.SetRow(exclusionsPanel, 4);
        Grid.SetColumnSpan(exclusionsPanel, 3);
        root.Children.Add(exclusionsPanel);

        var shell = new DockPanel { LastChildFill = true };
        var feedback = new StackPanel();
        DockPanel.SetDock(feedback, Dock.Bottom);
        selectedWindowText.TextWrapping = TextWrapping.Wrap;
        selectedWindowText.FontWeight = FontWeight.SemiBold;
        selectedWindowText.Margin = new Thickness(0, 10, 0, 0);
        feedback.Children.Add(selectedWindowText);
        statusText.Margin = new Thickness(0, 10, 0, 0);
        statusText.TextWrapping = TextWrapping.Wrap;
        feedback.Children.Add(statusText);
        shell.Children.Add(feedback);
        shell.Children.Add(root);
        return shell;
    }

    private Button AddAction(Panel parent, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 8, 8) };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error)
            {
                lastMessage = $"Command was not sent: {error.Message}";
                UpdateActionStates();
            }
        };
        parent.Children.Add(button);
        return button;
    }

    private static void AddPanel(Grid grid, string title, ListBox list, int column, int row)
    {
        var panel = MakePanel(title, list);
        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, row);
        grid.Children.Add(panel);
    }

    private static Border MakePanel(string title, Control content)
    {
        var stack = new Grid { RowDefinitions = new RowDefinitions { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)) } };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        Grid.SetRow(content, 1);
        stack.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(12), Margin = new Thickness(0), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Child = stack
        };
    }

    private static void ConfigureItems(ListBox list) => list.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
        new TextBlock { Text = item?.ToString() ?? "", TextWrapping = TextWrapping.Wrap });

    private void WireSelection(ListBox list)
    {
        ConfigureItems(list);
        list.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(list, monitors)) selectedMonitorId = SelectedMonitorId();
            if (ReferenceEquals(list, workspaces)) selectedWorkspaceId = SelectedWorkspaceId();
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
                return new IdentifiedItem(workspace.Id, $"{workspace.Name} · {view} · {workspace.WindowIds.Count} windows");
            }), selectedWorkspaceId);
        selectedWindowId = Populate(windows, next.Desktop.Windows.Select(window =>
        {
            var monitorName = next.Desktop.Monitors.FirstOrDefault(monitor => monitor.Id == window.MonitorId)?.DeviceName ?? "unknown monitor";
            var workspace = next.Workspaces?.FirstOrDefault(candidate => candidate.WindowIds.Contains(window.Id));
            var state = InspectorPresentation.WindowState(window);
            return new IdentifiedItem(window.Id,
                $"{window.Title}\n{window.ProcessName} / {window.ClassName} · {window.Mode} · {state}\nPhysical: {monitorName} · Workspace: {workspace?.Name ?? "unassigned"} · {window.Bounds.Width}×{window.Bounds.Height} · minimum {window.MinimumWidth}×{window.MinimumHeight}");
        }), selectedWindowId);

        exclusions.Items.Clear();
        if (next.Desktop.ConfiguredExclusions is null)
            exclusions.Items.Add("Unavailable: this manager version does not publish configured-exclusion diagnostics.");
        else if (next.Desktop.ConfiguredExclusions.Count == 0)
            exclusions.Items.Add("No windows matched configured process/class exclusions.");
        else
            foreach (var excluded in next.Desktop.ConfiguredExclusions)
            {
                var monitor = next.Desktop.Monitors.FirstOrDefault(candidate => candidate.Id == excluded.MonitorId)?.DeviceName ?? "unknown monitor";
                var reasons = new List<string>();
                if (excluded.ProcessNameExcluded) reasons.Add("configured process name");
                if (excluded.ClassNameExcluded) reasons.Add("configured class name");
                exclusions.Items.Add($"{excluded.Title} · {excluded.ProcessName} / {excluded.ClassName} · {string.Join(" + ", reasons)} · {monitor}");
            }
        if (connected) connectionText.Text = $"Connected · revision {next.StateRevision}";
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

    private void UpdateActionStates()
    {
        var canCommand = connected && snapshot is not null && !demoMode && !connecting && !commandBusy;
        connectButton.IsEnabled = !demoMode && !connecting && !connected && !commandBusy;
        activateButton.IsEnabled = canCommand && SelectedWorkspaceId() is not null && SelectedMonitorId() is not null;
        moveButton.IsEnabled = canCommand && SelectedWindowId() is not null && SelectedWorkspaceId() is not null;
        focusButton.IsEnabled = canCommand && SelectedWindowId() is not null;
        var selectedWindow = snapshot?.Desktop.Windows.FirstOrDefault(window => window.Id == SelectedWindowId());
        selectedWindowText.Text = InspectorPresentation.SelectedWindow(selectedWindow);
        tilingButton.IsEnabled = InspectorPresentation.CanSetMode(selectedWindow, WindowMode.Tiling, canCommand);
        floatingButton.IsEnabled = InspectorPresentation.CanSetMode(selectedWindow, WindowMode.Floating, canCommand);
        pauseButton.IsEnabled = canCommand;
        redrawButton.IsEnabled = canCommand;
        pauseButton.Content = snapshot?.Paused == true ? "Resume manager" : "Pause manager";
        statusText.Text = lastMessage ?? (demoMode
            ? "Demo mode: sample state only; actions are disabled."
            : connected ? "Live snapshot updates are subscribed. Commands are sent once; reconnect and inspect state if a connection is lost."
            : connecting ? "Connecting…" : "Disconnected. Use Connect / reconnect to request a fresh snapshot.");
        if (statusText.Text?.Length > 1200) statusText.Text = statusText.Text[..1200] + "…";
    }

    private async Task ConnectAsync()
    {
        if (demoMode || connecting || connected) return;
        connecting = true;
        lastMessage = null;
        connectionText.Text = "Connecting to the local manager…";
        UpdateActionStates();
        WinManagerClient? nextClient = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            nextClient = await WinManagerClient.ConnectAsync(pipeName, timeout: TimeSpan.FromSeconds(5),
                cancellationToken: lifetime.Token, eventQueueCapacity: 64);
            var nextSubscription = await nextClient.SubscribeAsync(cancellationToken: deadline.Token);
            var connectedClient = nextClient;
            client = connectedClient;
            nextClient = null;
            connected = true;
            connectionText.Text = "Connected · waiting for snapshot";
            UpdateActionStates();
            _ = PumpEventsAsync(connectedClient, nextSubscription, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            lastMessage = $"Connect failed: {error.Message}";
            connectionText.Text = snapshot is null ? "Disconnected" : "Disconnected · snapshot is stale";
        }
        finally
        {
            if (nextClient is not null) await nextClient.DisposeAsync();
            connecting = false;
            UpdateActionStates();
        }
    }

    private async Task PumpEventsAsync(WinManagerClient source, ManagerSubscription subscription, CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            await foreach (var envelope in subscription.ReadEventsAsync(cancellationToken))
            {
                var next = envelope.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)
                    ?? throw new InvalidDataException("Manager event did not contain a snapshot.");
                await Dispatcher.UIThread.InvokeAsync(() => ApplySnapshot(next));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { failure = error.Message; }
        finally
        {
            await source.DisposeAsync();
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(client, source)) return;
                    client = null;
                    connected = false;
                    connectionText.Text = "Disconnected · snapshot is stale";
                    var disconnectMessage = failure is null
                        ? "Connection closed. Reconnect for a fresh snapshot; no missed commands are replayed."
                        : $"Connection lost: {failure}. Command outcome may be unknown; reconnect and inspect state.";
                    lastMessage = lastMessage is null ? disconnectMessage : $"{lastMessage}\n{disconnectMessage}";
                    UpdateActionStates();
                });
            }
        }
    }

    private async Task ExecuteAsync(ManagerCommand command)
    {
        var activeClient = client;
        if (!connected || activeClient is null || commandBusy) return;
        var target = snapshot?.Desktop.Windows.FirstOrDefault(window => window.Id == command.WindowId);
        var description = InspectorPresentation.CommandDescription(command, target);
        commandBusy = true;
        lastMessage = InspectorPresentation.CommandFeedback(description, "Sending command…");
        UpdateActionStates();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await activeClient.ExecuteAsync(command, deadline.Token);
            lastMessage = InspectorPresentation.CommandFeedback(description, result.Success
                ? $"Command completed · revision {result.StateRevision}"
                : $"Command failed · {result.ErrorCode}: {result.Message}");
        }
        catch (Exception error)
        {
            lastMessage = InspectorPresentation.CommandFeedback(description, activeClient.IsConnected
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
        if (client is { } current) await current.DisposeAsync();
    }
}
