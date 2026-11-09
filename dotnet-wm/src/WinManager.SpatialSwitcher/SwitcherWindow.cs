using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinManager.Contracts;
using WinManager.Ui;

namespace WinManager.SpatialSwitcher;

internal sealed class SwitcherWindow : Window
{
    private sealed record MonitorChoice(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly IReadOnlyList<SolidColorBrush> WindowCardBrushes = SwitcherTheme.WindowCardPalette;

    private readonly bool demoMode;
    private readonly string? pipeName;
    private readonly Action<string, string>? trayStatusChanged;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Canvas map = new() { ClipToBounds = true };
    private readonly TextBox search = new();
    private readonly TextBlock resultCount = new();
    private readonly TextBlock selectedDetails = new();
    private readonly TextBlock status = new();
    private readonly TextBlock shortcutStatus = new();
    private readonly ComboBox monitorTarget = new();
    private readonly WorkspaceDetailsControl workspaceDetails;
    private readonly CurrentWorkspaceWindow currentWorkspaceWindow;
    private Task? connectionRetry;
    private Button activateButton = null!;
    private Button reconnectButton = null!;
    private readonly Dictionary<Guid, Button> workspaceTiles = [];
    private SwitcherKeyboardHook? keyboardHook;
    private ManagerSession? session;
    private ManagerSnapshot? snapshot;
    private Guid? selectedWorkspaceId;
    private Guid? selectedWindowId;
    private readonly SwitcherPopupContextState popupContext = new();
    private bool windowSelectionMode;
    private bool keyboardHookAttempted;
    private bool connected;
    private bool connecting;
    private bool commandBusy;
    private bool allowClose;
    private string? lastMessage;

    public SwitcherWindow(bool demoMode, string? pipeName = null, Action<string, string>? trayStatusChanged = null)
    {
        this.demoMode = demoMode;
        this.pipeName = pipeName;
        this.trayStatusChanged = trayStatusChanged;
        workspaceDetails = new WorkspaceDetailsControl(ExecuteWorkspaceLayoutAsync);
        currentWorkspaceWindow = new CurrentWorkspaceWindow(FocusWindowAsync, ExecuteWorkspaceLayoutAsync);
        Title = "Spatial switcher — One desk";
        Width = 716;
        Height = 592;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Content = BuildContent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            if (IsVisible) Hide();
        };
        Closing += (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            Hide();
        };
        Closed += (_, _) =>
        {
            keyboardHook?.Dispose();
            keyboardHook = null;
            _ = CloseConnectionAsync();
        };
    }

    public void Initialize()
    {
        if (demoMode)
        {
            ApplySnapshot(CreateDemoSnapshot());
            status.Text = "DEMO · sample state only · activation disabled";
            shortcutStatus.Text = "Demo mode · native keyboard interception disabled";
        }
        else
        {
            shortcutStatus.Text = "Waiting for a manager snapshot before enabling shortcuts";
            connectionRetry = RunConnectionRetryAsync();
        }

        UpdateActions();
    }

    public void ShowWorkspaceSwitcher()
    {
        var (placement, context) = CapturePopupContext();
        currentWorkspaceWindow.Hide();
        PopupPlacement.Place(this, placement);
        ApplyPopupContext(context);
        windowSelectionMode = false;
        UpdateSelectedDetails();
        UpdateActions();
        ShowPopup();
    }

    public void ShowWindowSwitcher()
    {
        ShowCurrentWorkspaceSwitcher(false);
    }

    public async Task PrepareForShutdownAsync()
    {
        allowClose = true;
        keyboardHook?.Dispose();
        keyboardHook = null;
        currentWorkspaceWindow.PrepareForShutdown();
        await CloseConnectionAsync();
    }

    private (PopupMonitor? Placement, SwitcherContext Context) CapturePopupContext()
    {
        var monitors = snapshot?.Desktop.Monitors ?? [];
        var nativePlacement = PopupPlacement.CaptureForegroundMonitor(monitors);
        var fallbackContext = snapshot is null ? default : SpatialSwitcherPresentation.CaptureContext(snapshot);
        var placement = nativePlacement ?? PopupPlacement.FromSnapshot(monitors, fallbackContext.MonitorId);
        var context = snapshot is null
            ? new SwitcherContext(null, null, placement?.MonitorId)
            : SpatialSwitcherPresentation.CaptureContext(snapshot, placement?.MonitorId);
        return (placement, context);
    }

    private void ApplyPopupContext(SwitcherContext context)
    {
        popupContext.Apply(context, PopupPlacement.FromSnapshot(snapshot?.Desktop.Monitors ?? [])?.MonitorId);
        selectedWorkspaceId = context.WorkspaceId ?? snapshot?.Workspaces?.FirstOrDefault()?.Id;
        selectedWindowId = context.WindowId;
        monitorTarget.SelectedItem = monitorTarget.Items.OfType<MonitorChoice>()
            .FirstOrDefault(choice => choice.Id == popupContext.MonitorId);
        RenderMap();
        UpdateSelectedDetails();
        UpdateActions();
    }

    private void ShowCurrentWorkspaceSwitcher(bool cycleWindow, bool previous = false)
    {
        var (placement, context) = CapturePopupContext();
        ApplyPopupContext(context);
        Hide();
        currentWorkspaceWindow.ShowForContext(snapshot, context, connected, placement, cycleWindow, previous);
    }

    private void ShowPopup()
    {
        Show();
        Activate();
        search.Focus();
    }

    private Control BuildContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto)
            },
            RowSpacing = 8
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        header.Children.Add(SwitcherTheme.Heading("Spatial switcher", 19));
        var keys = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        keys.Children.Add(KeyChip("Alt+Tab"));
        keys.Children.Add(SwitcherTheme.Caption("windows"));
        keys.Children.Add(KeyChip("Win+Tab / Space"));
        keys.Children.Add(SwitcherTheme.Caption("workspaces"));
        keys.Children.Add(KeyChip("Enter / Space"));
        keys.Children.Add(SwitcherTheme.Caption("commit"));
        Grid.SetColumn(keys, 1);
        header.Children.Add(keys);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var searchGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        search.PlaceholderText = "Find a workspace…";
        search.Padding = new Thickness(12, 8);
        search.FontSize = 14;
        search.TextChanged += (_, _) => RenderMap();
        searchGrid.Children.Add(search);
        var clearButton = SwitcherTheme.Action("Clear");
        clearButton.Margin = new Thickness(8, 0, 0, 0);
        clearButton.Click += (_, _) => search.Text = "";
        Grid.SetColumn(clearButton, 1);
        searchGrid.Children.Add(clearButton);
        Grid.SetRow(searchGrid, 1);
        root.Children.Add(searchGrid);

        var explanation = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        explanation.Children.Add(
            SwitcherTheme.Caption("Desk positions are shared logical space · search dims, never rearranges"));
        resultCount.HorizontalAlignment = HorizontalAlignment.Right;
        resultCount.Foreground = SwitcherTheme.MutedText;
        explanation.Children.Add(resultCount);
        Grid.SetRow(explanation, 2);
        root.Children.Add(explanation);

        var mapBorder = SwitcherTheme.Card(map, new Thickness(0));
        mapBorder.ClipToBounds = true;
        map.SizeChanged += (_, _) => RenderMap();
        Grid.SetRow(mapBorder, 3);
        root.Children.Add(mapBorder);

        var preview = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1.5, GridUnitType.Star)), new(new GridLength(1, GridUnitType.Star)) },
            ColumnSpacing = 16
        };
        selectedDetails.TextWrapping = TextWrapping.Wrap;
        var selection = new StackPanel { Spacing = 7 };
        selection.Children.Add(selectedDetails);
        selection.Children.Add(workspaceDetails);
        preview.Children.Add(selection);
        var destination = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        destination.Children.Add(SwitcherTheme.Caption("Activate on monitor"));
        monitorTarget.MinWidth = 190;
        monitorTarget.SelectionChanged += (_, _) =>
        {
            popupContext.SelectMonitor((monitorTarget.SelectedItem as MonitorChoice)?.Id);
            UpdateActions();
        };
        destination.Children.Add(monitorTarget);
        activateButton = SwitcherTheme.Action("Activate workspace", primary: true);
        activateButton.Click += async (_, _) => await CommitSelectionAsync();
        destination.Children.Add(activateButton);
        Grid.SetColumn(destination, 1);
        preview.Children.Add(destination);
        var previewCard = SwitcherTheme.Card(preview, new Thickness(12, 9));
        Grid.SetRow(previewCard, 4);
        root.Children.Add(previewCard);

        var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        hints.Children.Add(Hint("↑ ↓ ← →", "move across desk"));
        hints.Children.Add(Hint("Shift", "reverse tab"));
        hints.Children.Add(Hint("Esc", "dismiss"));
        Grid.SetRow(hints, 5);
        root.Children.Add(hints);

        shortcutStatus.TextWrapping = TextWrapping.Wrap;
        shortcutStatus.Foreground = SwitcherTheme.MutedText;
        Grid.SetRow(shortcutStatus, 6);
        root.Children.Add(shortcutStatus);

        var connectionStatus = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        status.Foreground = SwitcherTheme.MutedText;
        status.TextWrapping = TextWrapping.Wrap;
        connectionStatus.Children.Add(status);
        reconnectButton = SwitcherTheme.Action("Connect / reconnect");
        reconnectButton.Click += async (_, _) => await ConnectAsync();
        Grid.SetColumn(reconnectButton, 1);
        connectionStatus.Children.Add(reconnectButton);
        Grid.SetRow(connectionStatus, 7);
        root.Children.Add(connectionStatus);
        return new Border
        {
            Background = SwitcherTheme.Canvas,
            BorderBrush = SwitcherTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = root
        };
    }

    private static Control KeyChip(string text) => new Border
    {
        Background = SwitcherTheme.SoftSurface,
        BorderBrush = SwitcherTheme.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 2),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = SwitcherTheme.Text }
    };

    private static Control Hint(string key, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(KeyChip(key));
        row.Children.Add(SwitcherTheme.Caption(label));
        return row;
    }

    private void StartKeyboardHook()
    {
        if (keyboardHookAttempted) return;
        keyboardHookAttempted = true;
        try
        {
            keyboardHook = new SwitcherKeyboardHook(action => Dispatcher.UIThread.Post(() => HandleShortcut(action)));
            shortcutStatus.Text = "Alt+Tab cycles windows · Win+Tab or Win+Space opens workspaces";
        }
        catch (Exception error)
        {
            shortcutStatus.Text = $"Keyboard shortcuts unavailable · {error.Message}";
        }
    }

    private async Task RunConnectionRetryAsync()
    {
        try
        {
            await ConnectionRetry.RunAsync(_ => ConnectAsync(), () => connected, TimeSpan.FromSeconds(5),
                static (interval, token) => Task.Delay(interval, token), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task ConnectAsync()
    {
        if (connected || connecting || demoMode) return;
        connecting = true;
        status.Text = "Connecting to the local manager…";
        UpdateActions();
        ManagerSession? next = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            next = await ManagerSession.ConnectAsync(pipeName, deadline.Token, eventQueueCapacity: 32);
            var connectedSession = next;
            session = connectedSession;
            next = null;
            connected = true;
            connectedSession.Start((nextSnapshot, eventType) => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(session, connectedSession)) return;
                    ApplySnapshot(nextSnapshot);
                    status.Text = $"Connected · {eventType} · revision {nextSnapshot.StateRevision}";
                }),
                failure => Dispatcher.UIThread.Post(() => HandleDisconnect(connectedSession, failure)), lifetime.Token);
            UpdateActions();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            status.Text = $"Disconnected · {error.Message}";
            UpdateActions();
        }
        finally
        {
            if (next is not null) await next.DisposeAsync();
            connecting = false;
            UpdateActions();
        }
    }

    private void HandleDisconnect(ManagerSession source, Exception? failure)
    {
        if (!ReferenceEquals(session, source)) return;
        session = null;
        connected = false;
        keyboardHook?.Dispose();
        keyboardHook = null;
        keyboardHookAttempted = false;
        shortcutStatus.Text = "Keyboard shortcuts unavailable while disconnected";
        lastMessage = failure is null
            ? "Connection closed · snapshot is stale · reconnect before activating."
            : $"Connection lost · {failure.Message} · command outcome may be unknown.";
        status.Text = lastMessage;
        if (snapshot is not null)
            currentWorkspaceWindow.UpdateSnapshot(snapshot, popupContext.WorkspaceId, popupContext.MonitorId, false);
        UpdateActions();
        _ = source.DisposeAsync();
    }

    private void ApplySnapshot(ManagerSnapshot next)
    {
        snapshot = next;
        if (!demoMode) StartKeyboardHook();
        var workspaces = next.Workspaces ?? [];
        var fallbackMonitorId = PopupPlacement.CaptureForegroundMonitor(next.Desktop.Monitors)?.MonitorId;
        var context = popupContext.Refresh(next, fallbackMonitorId);
        if (selectedWorkspaceId is null || workspaces.All(workspace => workspace.Id != selectedWorkspaceId))
            selectedWorkspaceId = popupContext.WorkspaceId ?? workspaces.FirstOrDefault()?.Id;

        var capturedWorkspace = workspaces.FirstOrDefault(workspace => workspace.Id == popupContext.WorkspaceId);
        var windowCandidates = SpatialSwitcherPresentation.WindowsForWorkspace(capturedWorkspace, next.Desktop.Windows);
        if (selectedWindowId is null || windowCandidates.All(window => window.Id != selectedWindowId))
            selectedWindowId = context.WindowId ?? windowCandidates.FirstOrDefault()?.Id;

        var currentMonitorId = popupContext.MonitorId ?? (monitorTarget.SelectedItem as MonitorChoice)?.Id ??
            context.MonitorId;
        var monitors = next.Desktop.Monitors.OrderBy(monitor => monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 ? 0 : 1)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(monitor => monitor.Id)
            .ToArray();
        monitorTarget.Items.Clear();
        foreach (var monitor in monitors)
            monitorTarget.Items.Add(new MonitorChoice(monitor.Id,
                $"{monitor.DeviceName} · {monitor.Bounds.Width}×{monitor.Bounds.Height}"));
        var selectedMonitor = monitors.FirstOrDefault(monitor => monitor.Id == currentMonitorId) ??
                              monitors.FirstOrDefault();
        monitorTarget.SelectedItem = monitorTarget.Items.Cast<MonitorChoice>()
            .FirstOrDefault(choice => choice.Id == selectedMonitor?.Id);
        popupContext.SelectMonitor(selectedMonitor?.Id ?? context.MonitorId);
        currentWorkspaceWindow.UpdateSnapshot(next, popupContext.WorkspaceId, popupContext.MonitorId, connected);
        RenderMap();
        UpdateSelectedDetails();
        UpdateActions();
    }

    private void RenderMap()
    {
        map.Children.Clear();
        workspaceTiles.Clear();
        if (snapshot?.Workspaces is not { Count: > 0 } workspaces)
        {
            resultCount.Text = "0 workspaces";
            map.Children.Add(SwitcherTheme.Caption("Waiting for workspace state…"));
            return;
        }

        if (!SpatialSwitcherPresentation.HasSpatialState(workspaces))
        {
            resultCount.Text = "spatial state unavailable";
            map.Children.Add(new TextBlock
            {
                Text =
                    "Spatial positions are not available from this manager snapshot. The map is disabled rather than inventing local workspace locations.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = SwitcherTheme.Warning,
                MaxWidth = Math.Max(240, map.Bounds.Width - 32),
                Margin = new Thickness(16)
            });
            return;
        }

        var query = search.Text;
        var matches = workspaces.Count(workspace => SpatialSwitcherPresentation.Matches(workspace, query));
        resultCount.Text = $"{matches} / {workspaces.Count}";
        var width = map.Bounds.Width > 0 ? map.Bounds.Width : 672;
        var height = map.Bounds.Height > 0 ? map.Bounds.Height : 333;
        for (var x = 28d; x < width; x += 59)
        for (var y = 22d; y < height; y += 57)
        {
            var point = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 2,
                Height = 2,
                Fill = SwitcherTheme.Border,
                Opacity = 0.75
            };
            Canvas.SetLeft(point, x);
            Canvas.SetTop(point, y);
            map.Children.Add(point);
        }

        var locations = SpatialSwitcherPresentation.MapPositions(workspaces, width, height);
        foreach (var workspace in workspaces)
        {
            var tile = CreateWorkspaceTile(workspace);
            var location = locations[workspace.Id];
            Canvas.SetLeft(tile, location.X);
            Canvas.SetTop(tile, location.Y);
            tile.Opacity = SpatialSwitcherPresentation.Matches(workspace, query) ? 1 : 0.22;
            map.Children.Add(tile);
            workspaceTiles.Add(workspace.Id, tile);
        }

        UpdateSelectedDetails();
    }

    private Button CreateWorkspaceTile(WorkspaceInfo workspace)
    {
        var card = new Button
        {
            Width = 141,
            Height = 112,
            Padding = new Thickness(0),
            Background = workspace.Id == selectedWorkspaceId ? SwitcherTheme.AccentSoft : SwitcherTheme.Surface,
            BorderBrush = workspace.Id == selectedWorkspaceId ? SwitcherTheme.Accent : SwitcherTheme.Border,
            BorderThickness = new Thickness(workspace.Id == selectedWorkspaceId ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        var content = new StackPanel { Margin = new Thickness(8, 7), Spacing = 3 };
        var miniature = new Canvas { Width = 103, Height = 57, ClipToBounds = true };
        var windows = snapshot!.Desktop.Windows.Where(window => workspace.WindowIds.Contains(window.Id) &&
                                                                window.Bounds.Width > 0 && window.Bounds.Height > 0)
            .ToArray();
        foreach (var window in windows)
        {
            var monitor = snapshot.Desktop.Monitors.FirstOrDefault(candidate => candidate.Id == window.MonitorId);
            if (monitor is null || monitor.WorkArea.Width <= 0 || monitor.WorkArea.Height <= 0) continue;
            var area = monitor.WorkArea;
            var tileWidth = Math.Clamp(window.Bounds.Width / (double)area.Width * miniature.Width, 6, miniature.Width);
            var tileHeight = Math.Clamp(window.Bounds.Height / (double)area.Height * miniature.Height, 6,
                miniature.Height);
            var left = Math.Clamp((window.Bounds.X - area.X) / (double)area.Width * miniature.Width, 0,
                miniature.Width - tileWidth);
            var top = Math.Clamp((window.Bounds.Y - area.Y) / (double)area.Height * miniature.Height, 0,
                miniature.Height - tileHeight);
            var shape = new Border
            {
                Width = tileWidth,
                Height = tileHeight,
                Background =
                    WindowCardBrushes[(int)(Math.Abs((long)window.Id.GetHashCode()) % WindowCardBrushes.Count)],
                BorderBrush = SwitcherTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2)
            };
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            miniature.Children.Add(shape);
        }

        content.Children.Add(miniature);
        content.Children.Add(new TextBlock
        {
            Text = workspace.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = SwitcherTheme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var view = workspace.Active
            ? $"{workspace.Layout} · {workspace.WindowIds.Count} windows · on {MonitorName(workspace.MonitorId)}"
            : $"{workspace.Layout} · {workspace.WindowIds.Count} windows · unviewed";
        content.Children.Add(new TextBlock { Text = view, FontSize = 9, Foreground = SwitcherTheme.MutedText });
        card.Content = content;
        card.Click += (_, _) => SelectWorkspace(workspace.Id);
        return card;
    }

    private string MonitorName(Guid monitorId) =>
        snapshot?.Desktop.Monitors.FirstOrDefault(monitor => monitor.Id == monitorId)?.DeviceName ?? "monitor";

    private void SelectWorkspace(Guid workspaceId)
    {
        selectedWorkspaceId = workspaceId;
        windowSelectionMode = false;
        RenderMap();
        UpdateSelectedDetails();
        UpdateActions();
    }

    private void UpdateSelectedDetails()
    {
        var workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == selectedWorkspaceId);
        workspaceDetails.UpdateWorkspace(workspace, connected && !demoMode && !commandBusy);
        if (workspace is null)
        {
            selectedDetails.Text = "Select a workspace";
            return;
        }

        foreach (var (id, tile) in workspaceTiles)
        {
            var selected = id == workspace.Id;
            tile.BorderBrush = selected ? SwitcherTheme.Accent : SwitcherTheme.Border;
            tile.Background = selected ? SwitcherTheme.AccentSoft : SwitcherTheme.Surface;
            tile.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        if (windowSelectionMode)
        {
            var window = snapshot?.Desktop.Windows.FirstOrDefault(candidate => candidate.Id == selectedWindowId);
            selectedDetails.Text = window is null
                ? $"{workspace.Name}  ·  No switchable windows in this workspace"
                : $"{window.Title}\n{workspace.Name}  ·  {window.Mode}" +
                  (window.LayoutOverflow ? "  ·  overflow, Enter restores and focuses" : "");
            return;
        }

        var view = workspace.Active
            ? $"Currently viewed on {MonitorName(workspace.MonitorId)}"
            : "Not currently viewed";
        selectedDetails.Text =
            $"{workspace.Name}  ·  {workspace.Layout}\n{workspace.WindowIds.Count} windows  ·  {view}";
    }

    private void UpdateActions()
    {
        workspaceDetails.SetAvailable(connected && !demoMode && !commandBusy);
        currentWorkspaceWindow.SetWorkspaceCommandsAvailable(connected && !demoMode && !commandBusy);
        var workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == selectedWorkspaceId);
        var capturedWorkspace =
            snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == popupContext.WorkspaceId);
        var selectedWindow = snapshot?.Desktop.Windows.FirstOrDefault(candidate => candidate.Id == selectedWindowId);
        var hasMap = SpatialSwitcherPresentation.HasSpatialState(snapshot?.Workspaces);
        var canActivate = connected && !demoMode && !commandBusy && workspace is not null &&
                          popupContext.MonitorId is { } monitorId && monitorId != Guid.Empty &&
                          snapshot?.Desktop.Monitors.Any(monitor => monitor.Id == monitorId) == true && hasMap;
        var canFocus = connected && !demoMode && !commandBusy && capturedWorkspace?.Active == true &&
                       selectedWindow is not null &&
                       CurrentWindowCandidates().Any(window => window.Id == selectedWindow.Id);
        activateButton.Content = windowSelectionMode ? "Focus selected window" : "Activate workspace";
        activateButton.IsEnabled = windowSelectionMode ? canFocus : canActivate;
        monitorTarget.IsEnabled = !windowSelectionMode && !commandBusy && snapshot?.Desktop.Monitors.Count > 0;
        reconnectButton.IsEnabled = !demoMode && !connecting && !connected && !commandBusy;
        if (demoMode) status.Text = lastMessage ?? "DEMO · sample state only · activation disabled";
        var connection = demoMode ? "Demo mode" :
            connected ? "Manager connected" :
            connecting ? "Connecting to manager" : "Manager disconnected";
        var shortcuts = demoMode
            ? "Alt+Tab / Win+Tab / Win+Space disabled in demo"
            : keyboardHook is not null
                ? "Alt+Tab windows · Win+Tab / Space workspaces"
                : "Alt+Tab / Win+Tab / Win+Space unavailable";
        trayStatusChanged?.Invoke(connection, shortcuts);
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (!WorkspaceDetailsControl.ShouldHandlePopupKey(workspaceDetails.HasKeyboardFocus, args.Key)) return;

        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Hide();
            return;
        }

        if (SwitcherAcceptKeyPolicy.IsAcceptKey(args.Key, args.KeyModifiers, search.IsFocused))
        {
            args.Handled = true;
            _ = CommitSelectionAsync();
            return;
        }

        var direction = args.Key switch
        {
            Key.Left => Direction.Left,
            Key.Right => Direction.Right,
            Key.Up => Direction.Up,
            Key.Down => Direction.Down,
            _ => (Direction?)null
        };
        if (direction is not { } movement || selectedWorkspaceId is not { } currentId ||
            snapshot?.Workspaces is not { } workspaces)
            return;
        if (SpatialSwitcherPresentation.DirectionalTarget(currentId, workspaces, movement) is { } target)
        {
            SelectWorkspace(target);
            args.Handled = true;
        }
    }

    private void HandleShortcut(SwitcherKeyboardAction action)
    {
        if (demoMode || !connected || keyboardHook is null || snapshot is null) return;
        if (action is SwitcherKeyboardAction.CycleWindowForward or SwitcherKeyboardAction.CycleWindowBackward)
        {
            if (currentWorkspaceWindow.IsVisible)
                currentWorkspaceWindow.CycleWindow(action == SwitcherKeyboardAction.CycleWindowBackward);
            else
                ShowCurrentWorkspaceSwitcher(true, action == SwitcherKeyboardAction.CycleWindowBackward);
            return;
        }

        if (!IsVisible)
        {
            ShowWorkspaceSwitcher();
            return;
        }

        switch (action)
        {
            case SwitcherKeyboardAction.CycleWorkspaceForward:
            case SwitcherKeyboardAction.CycleWorkspaceBackward:
                windowSelectionMode = false;
                selectedWorkspaceId = SpatialSwitcherPresentation.CycleWorkspace(selectedWorkspaceId,
                    snapshot.Workspaces ?? [], action == SwitcherKeyboardAction.CycleWorkspaceBackward);
                RenderMap();
                UpdateSelectedDetails();
                UpdateActions();
                break;
        }
    }

    private Task<CommandResult> FocusWindowAsync(Guid windowId)
    {
        if (!connected || session is not { } activeSession)
            throw new InvalidOperationException("The manager is disconnected. Reconnect before focusing a window.");
        return activeSession.ExecuteAsync(SpatialSwitcherPresentation.FocusCommand(windowId), lifetime.Token);
    }

    private async Task<CommandResult> ExecuteWorkspaceLayoutAsync(ManagerCommand command)
    {
        var revision = snapshot?.StateRevision ?? 0;
        if (!connected || demoMode || commandBusy || command.Kind != CommandKind.SetWorkspaceLayout ||
            command.WorkspaceId is not { } workspaceId ||
            snapshot?.Workspaces?.Any(workspace => workspace.Id == workspaceId) != true ||
            session is not { IsConnected: true } activeSession)
            return new(false, "not_ready",
                "Workspace layout changes require a connected manager and a current workspace.",
                revision);

        var workspace = snapshot.Workspaces.First(candidate => candidate.Id == workspaceId);
        commandBusy = true;
        status.Text = $"Updating {workspace.Name} layout…";
        UpdateActions();
        try
        {
            var result = await activeSession.ExecuteAsync(command, lifetime.Token);
            status.Text = result.Success
                ? $"Updated {workspace.Name} layout · revision {result.StateRevision}"
                : $"Workspace layout update failed · {result.ErrorCode}: {result.Message}";
            return result;
        }
        catch (Exception error)
        {
            status.Text = activeSession.IsConnected
                ? $"Workspace layout update failed · {error.Message}"
                : $"Connection lost during layout update · outcome may be unknown. Reconnect and inspect state. {error.Message}";
            throw;
        }
        finally
        {
            commandBusy = false;
            UpdateActions();
        }
    }

    private IReadOnlyList<WindowInfo> CurrentWindowCandidates()
    {
        var workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == popupContext.WorkspaceId);
        return SpatialSwitcherPresentation.WindowsForWorkspace(workspace, snapshot?.Desktop.Windows ?? []);
    }

    private async Task CommitSelectionAsync()
    {
        if (windowSelectionMode) await FocusSelectedWindowAsync();
        else await ActivateSelectedAsync();
    }

    private async Task FocusSelectedWindowAsync()
    {
        if (!connected || demoMode || commandBusy || selectedWindowId is not { } windowId ||
            session is not { } activeSession || !CurrentWindowCandidates().Any(window => window.Id == windowId))
            return;
        if (snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == popupContext.WorkspaceId)?.Active != true)
            return;

        var window = snapshot?.Desktop.Windows.FirstOrDefault(candidate => candidate.Id == windowId);
        if (window is null) return;
        commandBusy = true;
        status.Text = $"Focusing {window.Title}…";
        UpdateActions();
        try
        {
            var result = await activeSession.ExecuteAsync(SpatialSwitcherPresentation.FocusCommand(windowId),
                lifetime.Token);
            if (result.Success)
            {
                lastMessage = $"Focused {window.Title} · revision {result.StateRevision}";
                status.Text = lastMessage;
                Hide();
            }
            else
            {
                lastMessage = $"Focus failed · {result.ErrorCode}: {result.Message}";
                status.Text = lastMessage;
            }
        }
        catch (Exception error)
        {
            lastMessage = activeSession.IsConnected
                ? $"Focus failed · {error.Message}"
                : $"Connection lost during focus · outcome may be unknown. Reconnect and inspect state. {error.Message}";
            status.Text = lastMessage;
        }
        finally
        {
            commandBusy = false;
            UpdateActions();
        }
    }

    private async Task ActivateSelectedAsync()
    {
        if (!connected || demoMode || commandBusy || selectedWorkspaceId is not { } workspaceId ||
            popupContext.MonitorId is not { } monitorId || session is not { } activeSession)
            return;

        var workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null) return;
        commandBusy = true;
        status.Text = $"Activating {workspace.Name} on {MonitorName(monitorId)}…";
        UpdateActions();
        try
        {
            var command = SpatialSwitcherPresentation.ActivationCommand(workspaceId, monitorId);
            var result = await activeSession.ExecuteAsync(command, lifetime.Token);
            if (result.Success)
            {
                lastMessage =
                    $"Activated {workspace.Name} on {MonitorName(monitorId)} · revision {result.StateRevision}";
                status.Text = lastMessage;
                Hide();
            }
            else
            {
                lastMessage = $"Activation failed · {result.ErrorCode}: {result.Message}";
                status.Text = lastMessage;
            }
        }
        catch (Exception error)
        {
            lastMessage = activeSession.IsConnected
                ? $"Activation failed · {error.Message}"
                : $"Connection lost during activation · outcome may be unknown. Reconnect and inspect state. {error.Message}";
            status.Text = lastMessage;
        }
        finally
        {
            commandBusy = false;
            UpdateActions();
        }
    }

    private ManagerSnapshot CreateDemoSnapshot()
    {
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var code = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var research = Guid.NewGuid();
        var media = Guid.NewGuid();
        var planning = Guid.NewGuid();
        var windowIds = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var monitors = new MonitorInfo[]
        {
            new(left, "DISPLAY-A", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96),
            new(right, "DISPLAY-B", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), 144)
        };
        WindowInfo[] windows =
        [
            new(windowIds[0], left, "Editor", "editor", "Editor", new(80, 80, 920, 820), true, false, false,
                WindowMode.Tiling),
            new(windowIds[1], left, "Terminal", "terminal", "Terminal", new(0, 0, 1920, 600), true, false, false,
                WindowMode.Tiling),
            new(windowIds[2], right, "Browser", "browser", "Browser", new(2040, 100, 1550, 880), true, false, false,
                WindowMode.Tiling),
            new(windowIds[3], right, "Notes", "notes", "Notes", new(2200, 200, 820, 650), true, false, false,
                WindowMode.Tiling),
            new(windowIds[4], left, "Chat", "chat", "Chat", new(130, 120, 640, 760), true, false, false,
                WindowMode.Tiling),
            new(windowIds[5], right, "Canvas", "canvas", "Canvas", new(1980, 120, 1820, 940), true, false, false,
                WindowMode.Tiling),
            new(windowIds[6], left, "Mail", "mail", "Mail", new(100, 140, 740, 650), false, true, false,
                WindowMode.Tiling)
        ];
        WorkspaceInfo[] workspaces =
        [
            new(code, "Code", left, true, [windowIds[0], windowIds[1], windowIds[6]], WorkspaceLayout.Layered,
                new(-420, -180)),
            new(chat, "Chat", right, true, [windowIds[4]], WorkspaceLayout.Split, new(40, -60)),
            new(research, "Research", Guid.Empty, false, [windowIds[2]], WorkspaceLayout.Split, new(510, -130)),
            new(media, "Media", Guid.Empty, false, [windowIds[3]], WorkspaceLayout.Layered, new(-280, 390)),
            new(planning, "Planning", Guid.Empty, false, [windowIds[5]], WorkspaceLayout.Split, new(360, 460))
        ];
        return new ManagerSnapshot(Guid.NewGuid(), 1, new DesktopSnapshot(monitors, windows, windowIds[0]),
            new Dictionary<Guid, SplitNodeInfo?>(), false, workspaces, null,
            [new(left, code), new(right, chat)]);
    }

    private async Task CloseConnectionAsync()
    {
        await lifetime.CancelAsync();
        if (connectionRetry is not null) await connectionRetry;
        var current = session;
        session = null;
        if (current is not null) await current.DisposeAsync();
    }
}