using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using HomeDesk.Client;
using HomeDesk.Contracts;

namespace HomeDeskManager;

internal sealed class CurrentWorkspaceWindow : Window
{
    private static readonly IBrush CanvasBrush = SwitcherTheme.Canvas;
    private static readonly IBrush SurfaceBrush = SwitcherTheme.Surface;
    private static readonly IBrush SelectedBrush = SwitcherTheme.AccentSoft;
    private static readonly IBrush CardBorderBrush = SwitcherTheme.Border;
    private static readonly IBrush AccentBrush = SwitcherTheme.Accent;
    private static readonly IBrush MutedBrush = SwitcherTheme.MutedText;

    private readonly Func<Guid, Task<CommandResult>> focusWindow;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets;

    private readonly StackPanel activeCards = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly StackPanel inactiveCards = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer activeScroll = new();
    private readonly ScrollViewer inactiveScroll = new();

    private readonly Border inactiveDivider = new()
    {
        Height = 1,
        Background = SwitcherTheme.Border,
        IsVisible = false
    };

    private readonly TextBlock emptyState = new();
    private readonly TextBlock feedback = new();
    private readonly Dictionary<Guid, WindowCardVisual> cards = [];
    private readonly Dictionary<Guid, WindowPreviewTarget> previewTargets = [];
    private readonly Dictionary<Guid, OwnedPreviewFrame> previewFrames = [];
    private readonly Dictionary<Guid, string> previewFailures = [];
    private readonly WindowRailSelectionState railSelection = new();
    private ManagerSnapshot? snapshot;
    private WorkspaceInfo? workspace;
    private WorkspaceWindowGroups groups = new([], []);
    private WindowPreviewCapture? previewCapture;
    private CancellationTokenSource? previewLookupCancellation;
    private WindowInfo[] requestedPreviewCandidates = [];
    private Guid? selectedWindowId;
    private bool connected;
    private bool commandBusy;
    private bool previewLookupPending;
    private bool allowClose;
    private Guid[] renderedActiveIds = [];
    private Guid[] renderedInactiveIds = [];

    public CurrentWorkspaceWindow(Func<Guid, Task<CommandResult>> focusWindow,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets)
    {
        this.focusWindow = focusWindow;
        this.queryPreviewTargets = queryPreviewTargets;
        Title = "Window switcher · One desk";
        Width = 980;
        Height = 520;
        MinWidth = 620;
        MinHeight = 390;
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
            if (IsVisible) Dismiss();
        };
        Closing += (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            Dismiss();
        };
        Closed += (_, _) => ReleasePreviewCapture();
    }

    public void PrepareForShutdown()
    {
        allowClose = true;
        if (IsVisible) Close();
        else ReleasePreviewCapture();
    }

    public void Dismiss()
    {
        CancelPreviewLookup();
        previewCapture?.SetTargets([]);
        previewCapture?.Pause();
        requestedPreviewCandidates = [];
        ClearPreviewPresentation();
        if (IsVisible) Hide();
    }

    public void ShowForContext(ManagerSnapshot? nextSnapshot, SwitcherContext context, bool managerConnected,
        PopupMonitor? placement, bool cycleWindow, bool previous = false)
    {
        CancelPreviewLookup();
        feedback.IsVisible = false;
        StartPreviewCapture();
        PopupPlacement.Place(this, placement);
        UpdateSnapshot(nextSnapshot, context.WorkspaceId, managerConnected);
        Topmost = false;
        Show();
        Topmost = true;
        Activate();
        if (cycleWindow) CycleWindow(previous);
        RefreshPreviewTargetsIfNeeded();
    }

    public void UpdateSnapshot(ManagerSnapshot? nextSnapshot, Guid? workspaceId, bool managerConnected)
    {
        var previousSelection = selectedWindowId;
        snapshot = nextSnapshot;
        connected = managerConnected;
        workspace = snapshot?.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        groups = SpatialSwitcherPresentation.WindowGroupsForWorkspace(workspace, snapshot?.Desktop.Windows ?? []);
        railSelection.UpdateGroups(groups);
        var candidates = Candidates();
        selectedWindowId = previousSelection is { } selected && candidates.Any(window => window.Id == selected)
            ? selected
            : snapshot?.Desktop.FocusedWindowId is { } focused && candidates.Any(window => window.Id == focused)
                ? focused
                : candidates.FirstOrDefault()?.Id;
        railSelection.ObserveSelection(selectedWindowId, groups);
        RenderWindows();
        if (IsVisible) RefreshPreviewTargetsIfNeeded();
    }

    public void CycleWindow(bool previous)
    {
        selectedWindowId = railSelection.CycleWindow(selectedWindowId, groups, previous);
        UpdateSelection();
    }

    private void SwitchWindowRail()
    {
        selectedWindowId = railSelection.SwitchRail(selectedWindowId, groups);
        UpdateSelection();
    }

    internal Guid? SelectedWindowId => selectedWindowId;

    private Control BuildContent()
    {
        var root = new Border
        {
            Background = CanvasBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18)
        };
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto)
            },
            RowSpacing = 10
        };

        var activeArea = new Grid();
        activeScroll.Content = activeCards;
        activeScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        activeScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        activeArea.Children.Add(activeScroll);
        emptyState.Foreground = MutedBrush;
        emptyState.HorizontalAlignment = HorizontalAlignment.Center;
        emptyState.VerticalAlignment = VerticalAlignment.Center;
        emptyState.IsVisible = false;
        activeArea.Children.Add(emptyState);
        layout.Children.Add(activeArea);

        Grid.SetRow(inactiveDivider, 1);
        layout.Children.Add(inactiveDivider);
        inactiveScroll.Content = inactiveCards;
        inactiveScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        inactiveScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        inactiveScroll.Height = 72;
        inactiveScroll.IsVisible = false;
        Grid.SetRow(inactiveScroll, 2);
        layout.Children.Add(inactiveScroll);

        feedback.Foreground = MutedBrush;
        feedback.TextTrimming = TextTrimming.CharacterEllipsis;
        feedback.IsVisible = false;
        Grid.SetRow(feedback, 3);
        layout.Children.Add(feedback);

        root.Child = layout;
        return root;
    }

    private void RenderWindows()
    {
        var activeIds = groups.Active.Select(window => window.Id).ToArray();
        var inactiveIds = groups.Inactive.Select(window => window.Id).ToArray();
        if (!activeIds.SequenceEqual(renderedActiveIds) || !inactiveIds.SequenceEqual(renderedInactiveIds))
        {
            foreach (var card in cards.Values) card.Dispose();
            cards.Clear();
            activeCards.Children.Clear();
            inactiveCards.Children.Clear();
            foreach (var window in groups.Active)
            {
                var card = CreateCard(window, inactive: false);
                cards.Add(window.Id, card);
                activeCards.Children.Add(card.Button);
            }

            foreach (var window in groups.Inactive)
            {
                var card = CreateCard(window, inactive: true);
                cards.Add(window.Id, card);
                inactiveCards.Children.Add(card.Button);
            }

            renderedActiveIds = activeIds;
            renderedInactiveIds = inactiveIds;
        }

        activeScroll.IsVisible = groups.Active.Count > 0;
        emptyState.IsVisible = groups.Active.Count == 0;
        emptyState.Text = workspace is null
            ? snapshot is null ? "Waiting for workspace state" : "No workspace on this display"
            : groups.Inactive.Count == 0
                ? "No windows in this workspace"
                : "No active windows";
        inactiveScroll.IsVisible = WindowSwitcherPresentation.HasInactiveWindows(groups.Inactive.Count);
        inactiveDivider.IsVisible = inactiveScroll.IsVisible;
        foreach (var window in Candidates())
        {
            if (!cards.TryGetValue(window.Id, out var card)) continue;
            card.UpdateWindow(window, window.Id == selectedWindowId, previewFrames.GetValueOrDefault(window.Id),
                PreviewMessage(window));
        }
    }

    private WindowCardVisual CreateCard(WindowInfo window, bool inactive)
    {
        var preview = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var placeholder = new TextBlock
        {
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var previewContent = new Grid
        {
            Background = SwitcherTheme.ShapeCanvas,
            ClipToBounds = true,
            Children = { preview, placeholder }
        };
        var title = new TextBlock
        {
            Text = WindowTitle(window),
            Foreground = SwitcherTheme.Text,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Control content;
        if (inactive)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions
                    { new(GridLength.Auto), new(new GridLength(1, GridUnitType.Star)) },
                ColumnSpacing = 10
            };
            var smallPreview = new Border
            {
                Width = 56,
                Height = 40,
                Background = SwitcherTheme.ShapeCanvas,
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Child = previewContent
            };
            row.Children.Add(smallPreview);
            title.FontSize = 12;
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            content = row;
        }
        else
        {
            var panel = new StackPanel { Spacing = 8 };
            var largePreview = new Border
            {
                Height = 142,
                Background = SwitcherTheme.ShapeCanvas,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                ClipToBounds = true,
                Child = previewContent
            };
            panel.Children.Add(largePreview);
            title.FontSize = 14;
            panel.Children.Add(title);
            content = panel;
        }

        var button = new Button
        {
            Width =
                inactive ? WindowSwitcherPresentation.InactiveCardWidth : WindowSwitcherPresentation.ActiveCardWidth,
            Height = inactive
                ? WindowSwitcherPresentation.InactiveCardHeight
                : WindowSwitcherPresentation.ActiveCardHeight,
            Padding = new Thickness(inactive ? 8 : 10),
            Background = SurfaceBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content
        };
        button.Click += (_, _) => SelectWindow(window.Id);
        AttachCardPointerHandler(button, clickCount => HandleCardPointerPressed(window.Id, clickCount));
        var visual = new WindowCardVisual(button, title, preview, placeholder);
        visual.UpdateWindow(window, window.Id == selectedWindowId, previewFrames.GetValueOrDefault(window.Id),
            PreviewMessage(window));
        return visual;
    }

    private void SelectWindow(Guid windowId)
    {
        selectedWindowId = windowId;
        UpdateSelection();
    }

    internal bool HandleCardPointerPressed(Guid windowId, int clickCount)
    {
        if (!WindowSwitcherPresentation.IsDoubleClick(clickCount) ||
            !Candidates().Any(window => window.Id == windowId)) return false;
        selectedWindowId = windowId;
        UpdateSelection();
        _ = FocusSelectedWindowAsync();
        return true;
    }

    internal static void AttachCardPointerHandler(Button button, Func<int, bool> handleCardPointerPressed)
    {
        button.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.Properties.IsLeftButtonPressed || !handleCardPointerPressed(args.ClickCount)) return;
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private void UpdateSelection()
    {
        railSelection.ObserveSelection(selectedWindowId, groups);
        foreach (var window in Candidates())
        {
            if (cards.TryGetValue(window.Id, out var card))
                card.SetSelected(window.Id == selectedWindowId);
        }

        if (selectedWindowId is { } selected && cards.TryGetValue(selected, out var selectedCard))
            selectedCard.Button.BringIntoView();
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Dismiss();
        }
        else if (WindowSwitcherPresentation.IsRailSwitchKey(args.Key, args.KeyModifiers))
        {
            args.Handled = true;
            SwitchWindowRail();
        }
        else if (WindowSwitcherPresentation.IsTabCycleKey(args.Key))
        {
            args.Handled = true;
            CycleWindow(WindowSwitcherPresentation.IsPreviousTabCycle(args.Key, args.KeyModifiers));
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
        commandBusy = true;
        feedback.IsVisible = false;
        try
        {
            var result = await focusWindow(targetId);
            if (result.Success)
            {
                Dismiss();
            }
            else
            {
                feedback.Text = $"Focus failed · {result.ErrorCode}: {result.Message}";
                feedback.IsVisible = true;
            }
        }
        catch (Exception error)
        {
            feedback.Text = $"Focus failed · {error.Message}";
            feedback.IsVisible = true;
        }
        finally
        {
            commandBusy = false;
            RenderWindows();
        }
    }

    private void RefreshPreviewTargetsIfNeeded()
    {
        WindowInfo[] candidates = connected && IsVisible
            ? Candidates().Where(window => window.Visible && !window.Minimized).ToArray()
            : [];
        if (candidates.SequenceEqual(requestedPreviewCandidates)) return;

        CancelPreviewLookup();
        requestedPreviewCandidates = candidates;
        var ids = candidates.Select(window => window.Id).Distinct().ToArray();
        previewTargets.Clear();
        previewFrames.Clear();
        previewFailures.Clear();
        previewCapture?.SetTargets([]);
        if (!IsVisible || !connected || ids.Length == 0)
        {
            previewLookupPending = false;
            previewCapture?.Pause();
            RenderWindows();
            return;
        }

        var cancellation = new CancellationTokenSource();
        previewLookupCancellation = cancellation;
        previewLookupPending = true;
        RenderWindows();
        _ = BindPreviewTargetsAsync(ids, cancellation);
    }

    private async Task BindPreviewTargetsAsync(IReadOnlyList<Guid> ids, CancellationTokenSource cancellation)
    {
        try
        {
            var targets = new List<WindowPreviewTarget>();
            foreach (var chunk in ids.Chunk(PreviewTargetsRequestRules.MaximumWindowIds))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var result = await queryPreviewTargets(chunk, cancellation.Token);
                targets.AddRange(result.Targets);
                foreach (var failure in result.Failures) previewFailures[failure.WindowId] = failure.Code;
            }

            if (cancellation.IsCancellationRequested || !ReferenceEquals(previewLookupCancellation, cancellation))
                return;
            previewTargets.Clear();
            foreach (var target in targets) previewTargets[target.WindowId] = target;
            previewCapture?.SetTargets(targets);
            previewLookupPending = false;
            previewLookupCancellation = null;
            SyncPreviewCache();
            previewCapture?.BeginSnapshotBatch(ids.Where(previewTargets.ContainsKey).ToArray());
            RenderWindows();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
                                          HomeDeskRemoteException or HomeDeskProtocolException)
        {
            if (!ReferenceEquals(previewLookupCancellation, cancellation)) return;
            previewLookupPending = false;
            previewLookupCancellation = null;
            foreach (var id in ids) previewFailures[id] = error.Message;
            RenderWindows();
        }
        finally
        {
            if (ReferenceEquals(previewLookupCancellation, cancellation)) previewLookupCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SyncPreviewCache()
    {
        var targetIds = previewTargets.Keys.ToHashSet();
        foreach (var pair in previewCapture?.LatestFrames ?? new Dictionary<Guid, OwnedPreviewFrame>())
        {
            if (targetIds.Contains(pair.Key)) previewFrames[pair.Key] = pair.Value;
        }

        foreach (var pair in previewCapture?.LatestFailures ?? new Dictionary<Guid, string>())
        {
            if (targetIds.Contains(pair.Key)) previewFailures[pair.Key] = pair.Value;
        }
    }

    private void OnPreviewFrameChanged(object? sender, PreviewFrameChangedEventArgs args)
    {
        if (sender is not WindowPreviewCapture capture) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !ReferenceEquals(previewCapture, capture) || !previewTargets.ContainsKey(args.WindowId))
                return;
            if (args.Frame is null)
            {
                previewFrames.Remove(args.WindowId);
                if (args.Failure is null) previewFailures.Remove(args.WindowId);
            }
            else
            {
                previewFrames[args.WindowId] = args.Frame;
                previewFailures.Remove(args.WindowId);
            }

            if (args.Failure is not null) previewFailures[args.WindowId] = args.Failure;
            RenderWindows();
        }, DispatcherPriority.Render);
    }

    private string PreviewMessage(WindowInfo window)
    {
        if (window.Minimized || !window.Visible) return "Preview unavailable";
        if (!previewTargets.ContainsKey(window.Id))
            return previewLookupPending ? "Finding preview…" : "Preview unavailable";
        return previewFailures.TryGetValue(window.Id, out var failure)
            ? $"Preview unavailable · {failure}"
            : "Loading preview…";
    }

    private void StartPreviewCapture()
    {
        if (previewCapture is null)
        {
            previewCapture = new WindowPreviewCapture();
            previewCapture.FrameChanged += OnPreviewFrameChanged;
        }

        previewCapture.Pause();
        previewCapture.SetTargets([]);
        requestedPreviewCandidates = [];
        ClearPreviewPresentation();
    }

    private void CancelPreviewLookup()
    {
        if (previewLookupCancellation is not { } cancellation) return;
        previewLookupCancellation = null;
        cancellation.Cancel();
    }

    private void ClearPreviewPresentation()
    {
        foreach (var card in cards.Values) card.ClearPreview();
        previewTargets.Clear();
        previewFrames.Clear();
        previewFailures.Clear();
        previewLookupPending = false;
    }

    private void ReleasePreviewCapture()
    {
        CancelPreviewLookup();
        if (previewCapture is { } capture)
        {
            previewCapture = null;
            capture.FrameChanged -= OnPreviewFrameChanged;
            capture.Dispose();
        }

        ClearPreviewPresentation();
        foreach (var card in cards.Values) card.Dispose();
        cards.Clear();
    }

    private static WriteableBitmap CreateAvaloniaBitmap(OwnedPreviewFrame frame)
    {
        var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = frame.PixelBytes.ToArray();
        var rowLength = checked(frame.Width * 4);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < frame.Height; row++)
            Marshal.Copy(pixels, row * rowLength, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                rowLength);
        return bitmap;
    }

    private IReadOnlyList<WindowInfo> Candidates() => groups.Active.Concat(groups.Inactive).ToArray();

    private static string WindowTitle(WindowInfo window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;

    internal static TextBlock Label(string text, double size, IBrush foreground,
        FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        FontWeight = weight ?? FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private sealed class WindowCardVisual(Button button, TextBlock title, Image preview, TextBlock placeholder)
        : IDisposable
    {
        private WriteableBitmap? bitmap;

        public Button Button { get; } = button;

        public void UpdateWindow(WindowInfo window, bool selected, OwnedPreviewFrame? frame, string? message)
        {
            title.Text = WindowTitle(window);
            SetSelected(selected);
            if (frame is not null && (bitmap is null || !ReferenceEquals(frame, currentFrame)))
            {
                var previous = bitmap;
                bitmap = CreateAvaloniaBitmap(frame);
                preview.Source = bitmap;
                placeholder.IsVisible = false;
                currentFrame = frame;
                previous?.Dispose();
            }

            if (frame is null)
            {
                ClearPreview();
                placeholder.Text = message ?? "Preview unavailable";
            }
        }

        private OwnedPreviewFrame? currentFrame;

        public void SetSelected(bool selected)
        {
            Button.Background = selected ? SelectedBrush : SurfaceBrush;
            Button.BorderBrush = selected ? AccentBrush : CardBorderBrush;
            Button.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        public void ClearPreview()
        {
            var previous = bitmap;
            bitmap = null;
            currentFrame = null;
            preview.Source = null;
            placeholder.IsVisible = true;
            previous?.Dispose();
        }

        public void Dispose() => ClearPreview();
    }
}

internal static class WindowSwitcherPresentation
{
    public const double ActiveCardWidth = 264;
    public const double ActiveCardHeight = 200;
    public const double InactiveCardWidth = 220;
    public const double InactiveCardHeight = 64;

    public static bool HasInactiveWindows(int count) => count > 0;

    public static bool IsTabCycleKey(Key key) => key == Key.Tab;

    public static bool IsRailSwitchKey(Key key, KeyModifiers modifiers) =>
        IsTabCycleKey(key) && modifiers.HasFlag(KeyModifiers.Control);

    public static bool IsPreviousTabCycle(Key key, KeyModifiers modifiers) =>
        IsTabCycleKey(key) && modifiers.HasFlag(KeyModifiers.Shift) &&
        !modifiers.HasFlag(KeyModifiers.Control);

    public static Guid? CycleWindow(Guid? selectedWindowId, WorkspaceWindowGroups groups, bool previous)
    {
        var candidates = SelectedRail(selectedWindowId, groups);
        return SpatialSwitcherPresentation.CycleWindow(selectedWindowId, candidates, previous);
    }

    private static IReadOnlyList<WindowInfo> SelectedRail(Guid? selectedWindowId, WorkspaceWindowGroups groups)
    {
        if (selectedWindowId is { } selected && groups.Active.Any(window => window.Id == selected))
            return groups.Active;
        if (selectedWindowId is { } inactive && groups.Inactive.Any(window => window.Id == inactive))
            return groups.Inactive;
        return groups.Active.Count > 0 ? groups.Active : groups.Inactive;
    }

    public static bool IsDoubleClick(int clickCount) => clickCount >= 2;
}

internal sealed class WindowRailSelectionState
{
    private Guid? activeSelectionId;
    private Guid? inactiveSelectionId;

    public void UpdateGroups(WorkspaceWindowGroups groups)
    {
        if (activeSelectionId is { } active && !groups.Active.Any(window => window.Id == active))
            activeSelectionId = null;
        if (inactiveSelectionId is { } inactive && !groups.Inactive.Any(window => window.Id == inactive))
            inactiveSelectionId = null;
    }

    public void ObserveSelection(Guid? selectedWindowId, WorkspaceWindowGroups groups)
    {
        if (selectedWindowId is not { } selected) return;
        if (groups.Active.Any(window => window.Id == selected)) activeSelectionId = selected;
        else if (groups.Inactive.Any(window => window.Id == selected)) inactiveSelectionId = selected;
    }

    public Guid? CycleWindow(Guid? selectedWindowId, WorkspaceWindowGroups groups, bool previous)
    {
        var selection = WindowSwitcherPresentation.CycleWindow(selectedWindowId, groups, previous);
        ObserveSelection(selection, groups);
        return selection;
    }

    public Guid? SwitchRail(Guid? selectedWindowId, WorkspaceWindowGroups groups)
    {
        var selectedIsActive = groups.Active.Any(window => window.Id == selectedWindowId);
        var selectedIsInactive = groups.Inactive.Any(window => window.Id == selectedWindowId);
        if (!selectedIsActive && !selectedIsInactive) selectedIsActive = groups.Active.Count > 0;
        ObserveSelection(selectedWindowId, groups);

        var targetRail = selectedIsActive ? groups.Inactive : groups.Active;
        if (targetRail.Count == 0) return selectedWindowId;

        var rememberedSelection = selectedIsActive ? inactiveSelectionId : activeSelectionId;
        var selection = targetRail.Any(window => window.Id == rememberedSelection)
            ? rememberedSelection
            : targetRail[0].Id;
        ObserveSelection(selection, groups);
        return selection;
    }
}