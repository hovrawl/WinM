using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WinManager.Client;
using WinManager.Contracts;
using WinManager.Windows;
using ContractPixelRect = WinManager.Contracts.PixelRect;

namespace WinManager.SpatialSwitcher;

internal sealed class CarouselWindow : Window
{
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets;
    private readonly Func<Guid, Task<CommandResult>> focusWindow;
    private readonly Canvas root = new() { ClipToBounds = true };
    private readonly Canvas cardsHost = new() { ClipToBounds = true };
    private readonly TextBlock status = new();
    private readonly Dictionary<Guid, DwmThumbnailSourceSize> sourceSizes = [];
    private readonly CarouselState state = new();
    private IReadOnlyList<WindowInfo> candidates = [];
    private ManagerSnapshot? snapshot;
    private PopupMonitor? placement;
    private DwmThumbnailRenderer? renderer;
    private double workAreaWidth;
    private double workAreaHeight;
    private double viewportLeft;
    private double viewportTop;
    private double viewportWidth;
    private double viewportHeight;
    private bool managerConnected;
    private CancellationTokenSource? openCancellation;

    public CarouselWindow(Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets,
        Func<Guid, Task<CommandResult>> focusWindow)
    {
        this.queryPreviewTargets = queryPreviewTargets;
        this.focusWindow = focusWindow;
        Title = "Live preview carousel experiment";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Background = SwitcherTheme.Canvas;
        Content = root;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            if (IsVisible) HideCarousel();
        };
        Closed += (_, _) =>
        {
            state.CloseExternally();
            CancelPreviewLookup();
            ReleaseThumbnails();
        };
    }

    public async Task ShowForContextAsync(ManagerSnapshot? nextSnapshot, SwitcherContext context,
        bool connected, PopupMonitor? targetPlacement)
    {
        if (!connected || nextSnapshot is null || context.WorkspaceId is not { } workspaceId)
        {
            status.Text = "A connected manager and current workspace are required.";
            return;
        }

        CancelPreviewLookup();
        ReleaseThumbnails();
        state.CloseExternally();
        var cancellation = new CancellationTokenSource();
        openCancellation = cancellation;
        snapshot = nextSnapshot;
        placement = targetPlacement;
        managerConnected = connected;
        var workspace = snapshot.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        candidates = SpatialSwitcherPresentation.WindowsForWorkspace(workspace, snapshot.Desktop.Windows);
        state.BeginOpen(context, candidates);
        sourceSizes.Clear();
        ConfigurePlacement();
        var selectedIndex = state.SelectedWindowId is { } selected
            ? Array.FindIndex(candidates.ToArray(), window => window.Id == selected)
            : 0;
        state.SetInitialOffset(Math.Max(0, selectedIndex) * CarouselPresentation.CardStride(workAreaWidth),
            CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth, viewportWidth));
        RenderCards();
        if (!IsVisible) Show();
        Activate();
        if (!state.MarkOpen()) return;

        var platformHandle = TryGetPlatformHandle()?.Handle ?? 0;
        if (platformHandle == 0)
        {
            status.Text = "The preview destination window handle is unavailable.";
            CancelPreviewLookup();
            return;
        }

        renderer = new DwmThumbnailRenderer(platformHandle);
        try
        {
            await BindPreviewsAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(openCancellation, cancellation))
            {
                openCancellation = null;
                cancellation.Dispose();
            }
        }

        if (IsVisible) RenderCards();
    }

    public async Task UpdateSnapshotAsync(ManagerSnapshot nextSnapshot, bool connected)
    {
        if (!IsVisible || state.Lifecycle != CarouselLifecycleState.Open) return;
        if (!connected)
        {
            HideCarousel();
            return;
        }

        if (state.Context is not { WorkspaceId: { } workspaceId, MonitorId: { } monitorId })
        {
            HideCarousel();
            return;
        }

        if (!IsCapturedViewValid(nextSnapshot, state.Context.Value))
        {
            HideCarousel();
            return;
        }

        var workspace = nextSnapshot.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null)
        {
            HideCarousel();
            return;
        }

        var nextCandidates = SpatialSwitcherPresentation.WindowsForWorkspace(workspace, nextSnapshot.Desktop.Windows);
        var candidatesChanged = !candidates.SequenceEqual(nextCandidates);
        var previousSelection = state.SelectedWindowId;
        snapshot = nextSnapshot;
        managerConnected = connected;
        candidates = nextCandidates;
        state.UpdateCandidates(candidates);
        var nextPlacement = PopupPlacement.FromSnapshot(nextSnapshot.Desktop.Monitors, monitorId);
        if (nextPlacement is null)
        {
            HideCarousel();
            return;
        }

        var placementChanged = placement != nextPlacement;
        placement = nextPlacement;
        if (placementChanged) ConfigurePlacement();
        var maximumOffset = CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth, viewportWidth);
        state.ClampOffset(maximumOffset);
        if (candidatesChanged || previousSelection != state.SelectedWindowId)
            state.ScrollToSelected(CarouselPresentation.CardStride(workAreaWidth), maximumOffset);

        if (candidatesChanged)
        {
            CancelPreviewLookup();
            foreach (var windowId in sourceSizes.Keys.ToArray()) renderer?.Release(windowId);
            sourceSizes.Clear();
            using var cancellation = new CancellationTokenSource();
            openCancellation = cancellation;
            try
            {
                await BindPreviewsAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(openCancellation, cancellation))
                {
                    openCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        if (IsVisible) RenderCards();
    }

    internal static bool IsCapturedViewValid(ManagerSnapshot nextSnapshot, SwitcherContext context)
    {
        if (context.WorkspaceId is not { } workspaceId || context.MonitorId is not { } monitorId ||
            !nextSnapshot.Desktop.Monitors.Any(monitor => monitor.Id == monitorId)) return false;
        var workspace = nextSnapshot.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        if (workspace is null) return false;
        var view = nextSnapshot.WorkspaceViews?.FirstOrDefault(candidate => candidate.MonitorId == monitorId);
        return view is not null
            ? view.WorkspaceId == workspaceId
            : workspace.Active && workspace.MonitorId == monitorId;
    }

    public void PrepareForShutdown()
    {
        CancelPreviewLookup();
        ReleaseThumbnails();
        state.CloseExternally();
        if (IsVisible) Close();
    }

    public void Dismiss() => HideCarousel();

    public void CancelSelection() => _ = CancelAsync();

    public void ScrollBy(double horizontalDelta)
    {
        if (!double.IsFinite(horizontalDelta) || horizontalDelta == 0) return;
        var maximumOffset = CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth, viewportWidth);
        var pixels = horizontalDelta * workAreaWidth * 1.8;
        if (state.ScrollBy(pixels, maximumOffset, CarouselPresentation.CardStride(workAreaWidth),
                CarouselPresentation.CardWidth(workAreaWidth), viewportWidth))
            RenderCards();
    }

    private void ConfigurePlacement()
    {
        var target = placement ?? PopupPlacement.FromSnapshot(snapshot!.Desktop.Monitors,
            SpatialSwitcherPresentation.CaptureContext(snapshot).MonitorId);
        placement = target;
        var scale = (target?.Dpi ?? 96u) / 96d;
        var workArea = target?.WorkArea ?? snapshot!.Desktop.Monitors.FirstOrDefault()?.WorkArea ??
            new ContractPixelRect(0, 0, 1280, 800);
        workAreaWidth = Math.Max(1, workArea.Width / scale);
        workAreaHeight = Math.Max(1, workArea.Height / scale);
        Width = Math.Min(workAreaWidth, Math.Max(480, workAreaWidth * 0.96));
        var previewHeight = workAreaHeight / 2;
        Height = Math.Min(workAreaHeight * 0.92, previewHeight + 196);
        MinWidth = Math.Min(480, Width);
        MinHeight = Math.Min(420, Height);
        viewportLeft = 28;
        viewportTop = 90;
        viewportWidth = Math.Max(0, Width - viewportLeft * 2);
        viewportHeight = Math.Max(0, Height - viewportTop - 78);
        PopupPlacement.Place(this, target);
    }

    private async Task BindPreviewsAsync(CancellationToken cancellationToken)
    {
        var ids = candidates.Where(window => window.Visible && !window.Minimized)
            .Select(window => window.Id).ToArray();
        if (!managerConnected || ids.Length == 0 || renderer is null)
        {
            status.Text = candidates.Count == 0
                ? "The captured workspace has no eligible windows."
                : "No live preview targets are available.";
            return;
        }

        var liveTargets = new List<WindowPreviewTarget>();
        var failed = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(PreviewTargetsRequestRules.MaximumWindowIds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await queryPreviewTargets(chunk, cancellationToken);
                liveTargets.AddRange(result.Targets);
                foreach (var failure in result.Failures) failed.Add(failure.WindowId);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                                              WinManagerRemoteException or WinManagerProtocolException)
            {
                status.Text = $"Live preview lookup failed · {error.Message}";
                return;
            }
        }

        foreach (var target in liveTargets)
        {
            if (renderer.TryBind(target, out var size, out _)) sourceSizes[target.WindowId] = size;
            else failed.Add(target.WindowId);
        }

        status.Text = failed.Count == 0
            ? $"DWM preview experiment · {sourceSizes.Count} live previews · Esc cancels"
            : $"DWM preview experiment · {sourceSizes.Count} live previews · {failed.Count} placeholders";
    }

    private void RenderCards()
    {
        root.Children.Clear();
        root.Width = Width;
        root.Height = Height;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(SwitcherTheme.Heading("Live preview carousel", 20));
        header.Children.Add(SwitcherTheme.Caption("DWM experiment · applications remain in their normal positions"));
        Canvas.SetLeft(header, 28);
        Canvas.SetTop(header, 26);
        root.Children.Add(header);

        cardsHost.Children.Clear();
        cardsHost.Width = viewportWidth;
        cardsHost.Height = viewportHeight;
        var layouts = CarouselPresentation.Layout(candidates, sourceSizes, workAreaWidth, workAreaHeight,
            viewportWidth, viewportHeight, state.HorizontalOffset);
        foreach (var layout in layouts)
        {
            var window = candidates.First(candidate => candidate.Id == layout.WindowId);
            var isSelected = layout.WindowId == state.SelectedWindowId;
            var card = CreateCard(window, layout, isSelected);
            Canvas.SetLeft(card, layout.CardBounds.X);
            Canvas.SetTop(card, layout.CardBounds.Y);
            cardsHost.Children.Add(card);
            if (!sourceSizes.ContainsKey(layout.WindowId)) continue;
            var sourceSize = sourceSizes[layout.WindowId];
            if (!CarouselPresentation.TryClipPreview(layout.PreviewBounds,
                    new Rect(0, 0, viewportWidth, viewportHeight), sourceSize, out var clip))
            {
                renderer?.TryUpdate(layout.WindowId, new ContractPixelRect(0, 0, 1, 1), visible: false);
                continue;
            }

            var scaling = RenderScaling;
            var destination = new ContractPixelRect(
                (int)Math.Round((viewportLeft + clip.DestinationBounds.X) * scaling),
                (int)Math.Round((viewportTop + clip.DestinationBounds.Y) * scaling),
                Math.Max(1, (int)Math.Round(clip.DestinationBounds.Width * scaling)),
                Math.Max(1, (int)Math.Round(clip.DestinationBounds.Height * scaling)));
            renderer?.TryUpdate(layout.WindowId, destination, sourceRectangle: clip.SourceBounds);
        }

        Canvas.SetLeft(cardsHost, viewportLeft);
        Canvas.SetTop(cardsHost, viewportTop);
        root.Children.Add(cardsHost);
        status.Foreground = SwitcherTheme.MutedText;
        status.TextWrapping = TextWrapping.NoWrap;
        Canvas.SetLeft(status, 28);
        Canvas.SetTop(status, Height - 48);
        root.Children.Add(status);
    }

    private Button CreateCard(WindowInfo window, CarouselCardLayout layout, bool selected)
    {
        var card = new Button
        {
            Width = layout.CardBounds.Width,
            Height = layout.CardBounds.Height,
            Padding = new Thickness(12),
            Background = selected ? SwitcherTheme.AccentSoft : SwitcherTheme.Surface,
            BorderBrush = selected ? SwitcherTheme.Accent : SwitcherTheme.Border,
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = BuildCardContent(window, layout)
        };
        card.Click += async (_, _) =>
        {
            if (!state.Select(window.Id)) return;
            RenderCards();
            await AcceptSelectionAsync();
        };
        return card;
    }

    private Control BuildCardContent(WindowInfo window, CarouselCardLayout layout)
    {
        var panel = new StackPanel { Spacing = 5 };
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title,
            Foreground = SwitcherTheme.Text,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = layout.CardBounds.Width - 24
        };
        panel.Children.Add(title);
        panel.Children.Add(SwitcherTheme.Caption(window.ProcessName));
        var preview = new Border
        {
            Width = Math.Max(1, layout.CardBounds.Width - 24),
            Height = Math.Max(1, layout.CardBounds.Height - 82),
            Background = SwitcherTheme.ShapeCanvas,
            BorderBrush = SwitcherTheme.ShapeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = SwitcherTheme.Caption(sourceSizes.ContainsKey(window.Id)
                ? "DWM thumbnail"
                : window.Minimized
                    ? "Live preview unavailable · Minimized"
                    : "Live preview unavailable")
        };
        panel.Children.Add(preview);
        return panel;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            _ = CancelAsync();
            return;
        }

        if (args.Key is Key.Enter or Key.Space)
        {
            args.Handled = true;
            _ = AcceptSelectionAsync();
            return;
        }

        if (args.Key is not (Key.Left or Key.Right) || candidates.Count == 0) return;
        var current = state.SelectedWindowId is { } id
            ? Array.FindIndex(candidates.ToArray(), window => window.Id == id)
            : -1;
        var next = args.Key == Key.Left ? Math.Max(0, current - 1) : Math.Min(candidates.Count - 1, current + 1);
        state.Select(candidates[next].Id);
        state.ScrollToSelected(CarouselPresentation.CardStride(workAreaWidth),
            CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth, viewportWidth));
        args.Handled = true;
        RenderCards();
    }

    private async Task AcceptSelectionAsync()
    {
        var command = state.Accept();
        if (command?.WindowId is not { } id || !candidates.Any(window => window.Id == id)) return;
        try
        {
            var result = await focusWindow(id);
            if (!result.Success)
            {
                state.AcceptanceFailed();
                status.Text = $"Focus failed · {result.ErrorCode}: {result.Message}";
                return;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
                                          WinManagerRemoteException or WinManagerProtocolException)
        {
            state.AcceptanceFailed();
            status.Text = $"Focus failed · {error.Message}";
            return;
        }

        HideCarousel();
    }

    private async Task CancelAsync()
    {
        if (state.Cancel() is { } original && managerConnected &&
            snapshot?.Desktop.Windows.Any(window => window.Id == original) == true)
        {
            try
            {
                await focusWindow(original);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                                              WinManagerRemoteException or WinManagerProtocolException)
            {
            }
        }

        HideCarousel();
    }

    private void HideCarousel()
    {
        state.CloseExternally();
        CancelPreviewLookup();
        ReleaseThumbnails();
        if (IsVisible) Hide();
    }

    private void CancelPreviewLookup()
    {
        if (openCancellation is not { } cancellation) return;
        openCancellation = null;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ReleaseThumbnails()
    {
        renderer?.Dispose();
        renderer = null;
    }
}