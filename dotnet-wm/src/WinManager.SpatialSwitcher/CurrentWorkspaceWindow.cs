using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WinManager.Contracts;

namespace WinManager.SpatialSwitcher;

internal sealed class CurrentWorkspaceWindow : Window
{
    private static readonly IBrush CanvasBrush = Brush("#171A1D");
    private static readonly IBrush SurfaceBrush = Brush("#22272B");
    private static readonly IBrush SelectedBrush = Brush("#2E3A34");
    private static readonly IBrush CardBorderBrush = Brush("#394146");
    private static readonly IBrush AccentBrush = Brush("#98B69F");
    private static readonly IBrush TextBrush = Brush("#F2F1EC");
    private static readonly IBrush MutedBrush = Brush("#9BA5A8");

    private static readonly IBrush[] ShapeBrushes =
    [
        Brush("#A9C6B0"), Brush("#D0B99A"), Brush("#AABDD1"), Brush("#C2AFCB")
    ];

    private readonly Func<Guid, Task<CommandResult>> focusWindow;
    private readonly StackPanel activeCards = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly StackPanel inactiveCards = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock workspaceTitle = new();
    private readonly TextBlock activeHeading = new();
    private readonly TextBlock inactiveHeading = new();
    private readonly TextBlock status = new();
    private Button focusButton = null!;
    private ManagerSnapshot? snapshot;
    private WorkspaceInfo? workspace;
    private WorkspaceWindowGroups groups = new([], []);
    private Guid? monitorId;
    private Guid? selectedWindowId;
    private bool connected;
    private bool commandBusy;
    private bool allowClose;

    public CurrentWorkspaceWindow(Func<Guid, Task<CommandResult>> focusWindow)
    {
        this.focusWindow = focusWindow;
        Title = "Current workspace · One desk";
        Width = 790;
        Height = 510;
        MinWidth = 620;
        MinHeight = 420;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
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
    }

    public void PrepareForShutdown()
    {
        allowClose = true;
        if (IsVisible) Close();
    }

    public void ShowForContext(ManagerSnapshot? nextSnapshot, SwitcherContext context, bool managerConnected,
        PopupMonitor? placement, bool cycleWindow, bool previous = false)
    {
        PopupPlacement.Place(this, placement);
        UpdateSnapshot(nextSnapshot, context.WorkspaceId, context.MonitorId, managerConnected);
        Show();
        Activate();
        if (cycleWindow) CycleWindow(previous);
    }

    public void UpdateSnapshot(ManagerSnapshot? nextSnapshot, Guid? workspaceId, Guid? targetMonitorId,
        bool managerConnected)
    {
        var previousSelection = selectedWindowId;
        snapshot = nextSnapshot;
        monitorId = targetMonitorId;
        connected = managerConnected;
        workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        groups = SpatialSwitcherPresentation.WindowGroupsForWorkspace(workspace, snapshot?.Desktop.Windows ?? []);
        var candidates = Candidates();
        selectedWindowId = previousSelection is { } selected && candidates.Any(window => window.Id == selected)
            ? selected
            : snapshot?.Desktop.FocusedWindowId is { } focused && candidates.Any(window => window.Id == focused)
                ? focused
                : candidates.FirstOrDefault()?.Id;
        RenderWindows();
    }

    public void CycleWindow(bool previous)
    {
        var candidates = Candidates();
        selectedWindowId = SpatialSwitcherPresentation.CycleWindow(selectedWindowId, candidates, previous);
        RenderWindows();
    }

    private Control BuildContent()
    {
        var root = new Border
        {
            Background = CanvasBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22)
        };
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new(GridLength.Auto), new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)),
                new(GridLength.Auto), new(GridLength.Auto)
            },
            RowSpacing = 11
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) }
        };
        var eyebrow = Label("CURRENT WORKSPACE", 10, MutedBrush, FontWeight.SemiBold);
        header.Children.Add(eyebrow);
        var close = new Button
        {
            Content = "×",
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = SurfaceBrush,
            Foreground = TextBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        workspaceTitle.FontSize = 21;
        workspaceTitle.FontWeight = FontWeight.SemiBold;
        workspaceTitle.Foreground = TextBrush;
        Grid.SetRow(workspaceTitle, 1);
        layout.Children.Add(workspaceTitle);

        var activeArea = new Grid
        {
            RowDefinitions = new RowDefinitions { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)) },
            RowSpacing = 8
        };
        activeHeading.Foreground = MutedBrush;
        activeHeading.FontSize = 10;
        activeHeading.FontWeight = FontWeight.SemiBold;
        activeArea.Children.Add(activeHeading);
        var activeScroll = new ScrollViewer
        {
            Content = activeCards,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetRow(activeScroll, 1);
        activeArea.Children.Add(activeScroll);
        Grid.SetRow(activeArea, 2);
        layout.Children.Add(activeArea);

        var inactiveBar = new Grid
        {
            RowDefinitions = new RowDefinitions { new(GridLength.Auto), new(GridLength.Auto) },
            RowSpacing = 7
        };
        inactiveHeading.Foreground = MutedBrush;
        inactiveHeading.FontSize = 10;
        inactiveHeading.FontWeight = FontWeight.SemiBold;
        inactiveBar.Children.Add(inactiveHeading);
        var inactiveScroll = new ScrollViewer
        {
            Content = inactiveCards,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetRow(inactiveScroll, 1);
        inactiveBar.Children.Add(inactiveScroll);
        Grid.SetRow(inactiveBar, 3);
        layout.Children.Add(inactiveBar);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
                { new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) },
            ColumnSpacing = 12
        };
        status.Foreground = MutedBrush;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(status);
        focusButton = new Button
        {
            Content = "Focus selected",
            Padding = new Thickness(17, 9),
            Background = AccentBrush,
            Foreground = Brush("#17201A"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            FontWeight = FontWeight.SemiBold
        };
        focusButton.Click += async (_, _) => await FocusSelectedWindowAsync();
        Grid.SetColumn(focusButton, 1);
        footer.Children.Add(focusButton);
        Grid.SetRow(footer, 4);
        layout.Children.Add(footer);

        root.Child = layout;
        return root;
    }

    private void RenderWindows()
    {
        activeCards.Children.Clear();
        inactiveCards.Children.Clear();

        if (workspace is null)
        {
            workspaceTitle.Text = "Waiting for workspace state";
            activeHeading.Text = "ACTIVE WINDOWS";
            inactiveHeading.Text = "INACTIVE WINDOWS";
            status.Text = snapshot is null
                ? "Waiting for a manager snapshot…"
                : "No workspace is assigned to this monitor.";
            focusButton.IsEnabled = false;
            return;
        }

        var targetMonitor = snapshot?.Desktop.Monitors.FirstOrDefault(candidate => candidate.Id == monitorId);
        workspaceTitle.Text =
            targetMonitor is null ? workspace.Name : $"{workspace.Name}  ·  {targetMonitor.DeviceName}";
        activeHeading.Text = $"ACTIVE WINDOWS  ·  {groups.Active.Count}";
        inactiveHeading.Text = $"INACTIVE WINDOWS  ·  {groups.Inactive.Count}";
        foreach (var window in groups.Active) activeCards.Children.Add(CreateActiveCard(window));
        foreach (var window in groups.Inactive) inactiveCards.Children.Add(CreateInactiveCard(window));
        if (groups.Active.Count == 0)
            activeCards.Children.Add(Label("No visible windows in this workspace", 13, MutedBrush));
        if (groups.Inactive.Count == 0)
            inactiveCards.Children.Add(Label("No inactive windows", 12, MutedBrush));

        var selected = Candidates().FirstOrDefault(window => window.Id == selectedWindowId);
        status.Text = selected is null
            ? connected ? "No switchable windows" : "Manager disconnected · focus unavailable"
            : connected
                ? $"{selected.Title}  ·  {ModeLabel(selected)}  ·  Alt+Tab to cycle · Enter / Space to focus · Esc to close"
                : "Manager disconnected · focus unavailable";
        focusButton.IsEnabled = connected && !commandBusy && selected is not null;
    }

    private Button CreateActiveCard(WindowInfo window)
    {
        var selected = window.Id == selectedWindowId;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(CreateWindowShape(window, 172, 94));
        content.Children.Add(new TextBlock
        {
            Text = window.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var card = WindowCard(content, selected, 190, 151);
        card.Click += (_, _) => SelectWindow(window.Id);
        return card;
    }

    private Button CreateInactiveCard(WindowInfo window)
    {
        var selected = window.Id == selectedWindowId;
        var detail = new TextBlock
        {
            Text = ModeLabel(window),
            FontSize = 9,
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = window.Title,
            FontSize = 12,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(detail);
        var card = WindowCard(content, selected, 166, 56);
        card.Click += (_, _) => SelectWindow(window.Id);
        return card;
    }

    private Border CreateWindowShape(WindowInfo window, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
        var area = snapshot?.Desktop.Monitors.FirstOrDefault(monitor => monitor.Id == window.MonitorId)?.WorkArea;
        var areaWidth = Math.Max(1, area?.Width ?? window.Bounds.Width);
        var areaHeight = Math.Max(1, area?.Height ?? window.Bounds.Height);
        var shapeWidth = Math.Clamp(window.Bounds.Width / (double)areaWidth * width, 28, width - 8);
        var shapeHeight = Math.Clamp(window.Bounds.Height / (double)areaHeight * height, 22, height - 8);
        var left = area is null
            ? (width - shapeWidth) / 2
            : Math.Clamp((window.Bounds.X - area.X) / (double)areaWidth * width, 3, width - shapeWidth - 3);
        var top = area is null
            ? (height - shapeHeight) / 2
            : Math.Clamp((window.Bounds.Y - area.Y) / (double)areaHeight * height, 3, height - shapeHeight - 3);
        var fill = ShapeBrushes[(int)(Math.Abs((long)window.Id.GetHashCode()) % ShapeBrushes.Length)];
        var shape = new Border
        {
            Width = shapeWidth,
            Height = shapeHeight,
            Background = fill,
            BorderBrush = Brush("#D9E5DC"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        canvas.Children.Add(shape);
        return new Border
        {
            Width = width,
            Height = height,
            Background = Brush("#141719"),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = canvas
        };
    }

    private static Button WindowCard(Control content, bool selected, double width, double height) => new()
    {
        Width = width,
        Height = height,
        Padding = new Thickness(8),
        Background = selected ? SelectedBrush : SurfaceBrush,
        BorderBrush = selected ? AccentBrush : CardBorderBrush,
        BorderThickness = new Thickness(selected ? 2 : 1),
        CornerRadius = new CornerRadius(10),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        Content = content
    };

    private void SelectWindow(Guid windowId)
    {
        selectedWindowId = windowId;
        RenderWindows();
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Hide();
        }
        else if (SwitcherAcceptKeyPolicy.IsAcceptKey(args.Key, args.KeyModifiers))
        {
            args.Handled = true;
            _ = FocusSelectedWindowAsync();
        }
        else if (args.Key is Key.Left or Key.Up)
        {
            args.Handled = true;
            CycleWindow(true);
        }
        else if (args.Key is Key.Right or Key.Down)
        {
            args.Handled = true;
            CycleWindow(false);
        }
    }

    private async Task FocusSelectedWindowAsync()
    {
        if (!connected || commandBusy || selectedWindowId is not { } targetId ||
            !Candidates().Any(window => window.Id == targetId)) return;
        var target = Candidates().First(window => window.Id == targetId);
        commandBusy = true;
        status.Text = $"Focusing {target.Title}…";
        focusButton.IsEnabled = false;
        try
        {
            var result = await focusWindow(targetId);
            if (result.Success)
            {
                status.Text = $"Focused {target.Title} · revision {result.StateRevision}";
                Hide();
            }
            else
            {
                status.Text = $"Focus failed · {result.ErrorCode}: {result.Message}";
            }
        }
        catch (Exception error)
        {
            status.Text = $"Focus failed · {error.Message}";
        }
        finally
        {
            commandBusy = false;
            RenderWindows();
        }
    }

    private IReadOnlyList<WindowInfo> Candidates() => groups.Active.Concat(groups.Inactive).ToArray();

    private static string ModeLabel(WindowInfo window) => window.LayoutOverflow
        ? "Overflow · Enter / Space to restore"
        : window.Minimized
            ? "Minimized"
            : !window.Visible
                ? "Not visible"
                : window.Mode.ToString();

    internal static TextBlock Label(string text, double size, IBrush foreground,
        FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        FontWeight = weight ?? FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}