using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HomeDesk.Client;
using HomeDesk.Contracts;
using System.Runtime.InteropServices;
using ContractPixelRect = HomeDesk.Contracts.PixelRect;

namespace HomeDeskManager;

internal sealed class CarouselWindow : Window
{
    private static readonly SolidColorBrush OverlayShade = new(Color.FromArgb(128, 0, 0, 0));
    private static readonly SolidColorBrush OverlayText = new(Color.Parse("#F7F7F7"));
    private static readonly SolidColorBrush OverlayMutedText = new(Color.Parse("#D6D6D6"));

    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets;
    private readonly Func<Guid, Task<CommandResult>> focusWindow;
    private readonly Canvas root = new() { ClipToBounds = true };
    private readonly Canvas cardsHost = new() { ClipToBounds = true };
    private readonly Border shade = new() { Background = OverlayShade, IsHitTestVisible = false };
    private readonly TextBlock status = new();
    private readonly Dictionary<Guid, PreviewImageSize> sourceSizes = [];
    private readonly Dictionary<Guid, OwnedPreviewFrame> previewFrames = [];
    private readonly Dictionary<Guid, string> previewFailures = [];
    private readonly Dictionary<Guid, WindowPreviewTarget> previewTargets = [];
    private readonly Dictionary<Guid, CardVisual> cardVisuals = [];
    private readonly CarouselState state = new();
    private readonly CarouselScrollMotion scrollMotion = new();
    private readonly DispatcherTimer scrollAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private IReadOnlyList<WindowInfo> candidates = [];
    private ManagerSnapshot? snapshot;
    private PopupMonitor? placement;
    private WindowPreviewCapture? previewCapture;
    private double workAreaWidth;
    private double workAreaHeight;
    private double viewportLeft;
    private double viewportTop;
    private double viewportWidth;
    private double viewportHeight;
    private bool managerConnected;
    private bool previewLookupPending;
    private CancellationTokenSource? openCancellation;
    private long lastScrollTick;

    public CarouselWindow(Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets,
        Func<Guid, Task<CommandResult>> focusWindow)
    {
        this.queryPreviewTargets = queryPreviewTargets;
        this.focusWindow = focusWindow;
        Title = "Window preview carousel";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Content = root;
        Canvas.SetLeft(status, 28);
        status.Foreground = OverlayMutedText;
        status.TextWrapping = TextWrapping.NoWrap;
        root.Children.Add(shade);
        root.Children.Add(cardsHost);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        scrollAnimationTimer.Tick += OnScrollAnimationTick;
        Deactivated += (_, _) =>
        {
            if (IsVisible) HideCarousel();
        };
        Closed += (_, _) =>
        {
            StopScrollMotion();
            state.CloseExternally();
            CancelPreviewLookup();
            ReleasePreviewCapture();
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

        StopScrollMotion();
        CancelPreviewLookup();
        StartPreviewCapture();
        state.CloseExternally();
        var cancellation = new CancellationTokenSource();
        openCancellation = cancellation;
        snapshot = nextSnapshot;
        placement = targetPlacement;
        managerConnected = connected;
        var workspace = snapshot.Workspaces?.FirstOrDefault(candidate => candidate.Id == workspaceId);
        candidates = SpatialSwitcherPresentation.WindowsForWorkspace(workspace, snapshot.Desktop.Windows);
        state.BeginOpen(context, candidates);
        previewLookupPending = managerConnected && candidates.Any(window => window.Visible && !window.Minimized);
        ConfigurePlacement();
        var selectedIndex = state.SelectedWindowId is { } selected
            ? Array.FindIndex(candidates.ToArray(), window => window.Id == selected)
            : 0;
        state.SetInitialOffset(Math.Max(0, selectedIndex) * CarouselPresentation.CardStride(workAreaWidth),
            CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth, viewportWidth));
        RenderCards();
        Topmost = false;
        if (!IsVisible) Show();
        Topmost = true;
        Activate();
        if (!state.MarkOpen()) return;

        try
        {
            await BindPreviewsAsync(cancellation.Token, beginSnapshotBatch: true);
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
        if (candidatesChanged) StopScrollMotion();
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
        if (placementChanged)
        {
            StopScrollMotion();
            ConfigurePlacement();
        }

        var maximumOffset = CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth, viewportWidth);
        state.ClampOffset(maximumOffset);
        if (candidatesChanged || previousSelection != state.SelectedWindowId)
            state.ScrollToSelected(CarouselPresentation.CardStride(workAreaWidth), maximumOffset);

        if (candidatesChanged)
        {
            CancelPreviewLookup();
            using var cancellation = new CancellationTokenSource();
            openCancellation = cancellation;
            try
            {
                await BindPreviewsAsync(cancellation.Token, beginSnapshotBatch: false);
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
        StopScrollMotion();
        CancelPreviewLookup();
        ReleasePreviewCapture();
        state.CloseExternally();
        if (IsVisible) Close();
    }

    public void Dismiss() => HideCarousel();

    public void AcceptSelection() => _ = AcceptSelectionAsync();

    public void CancelSelection() => _ = CancelAsync();

    public void ScrollBy(double horizontalDelta)
    {
        if (!double.IsFinite(horizontalDelta) || horizontalDelta == 0) return;
        scrollMotion.AddInput(horizontalDelta * workAreaWidth * 1.8);
        if (scrollAnimationTimer.IsEnabled) return;
        lastScrollTick = Stopwatch.GetTimestamp();
        scrollAnimationTimer.Start();
    }

    private void ConfigurePlacement()
    {
        var target = placement ?? PopupPlacement.FromSnapshot(snapshot!.Desktop.Monitors,
                SpatialSwitcherPresentation.CaptureContext(snapshot).MonitorId) ??
            new PopupMonitor(null, new ContractPixelRect(0, 0, 1280, 800), 96,
                new ContractPixelRect(0, 0, 1280, 800));
        placement = target;
        var scale = target.Dpi / 96d;
        workAreaWidth = Math.Max(1, target.MonitorBounds.Width / scale);
        workAreaHeight = Math.Max(1, target.MonitorBounds.Height / scale);
        Width = workAreaWidth;
        Height = workAreaHeight;
        MinWidth = Math.Min(480, Width);
        MinHeight = Math.Min(420, Height);
        viewportLeft = 28;
        viewportTop = 90;
        viewportWidth = Math.Max(0, Width - viewportLeft * 2);
        viewportHeight = Math.Max(0, Height - viewportTop - 78);
        root.Width = Width;
        root.Height = Height;
        shade.Width = Width;
        shade.Height = Height;
        cardsHost.Width = viewportWidth;
        cardsHost.Height = viewportHeight;
        Canvas.SetLeft(cardsHost, viewportLeft);
        Canvas.SetTop(cardsHost, viewportTop);
        Canvas.SetTop(status, Height - 48);
        PopupPlacement.CoverMonitor(this, target);
    }

    private async Task BindPreviewsAsync(CancellationToken cancellationToken, bool beginSnapshotBatch)
    {
        var ids = candidates.Where(window => window.Visible && !window.Minimized)
            .Select(window => window.Id).ToArray();
        previewLookupPending = true;
        if (!managerConnected || ids.Length == 0 || previewCapture is null)
        {
            previewLookupPending = false;
            previewTargets.Clear();
            previewCapture?.SetTargets([]);
            SyncPreviewCache();
            if (beginSnapshotBatch) previewCapture?.BeginSnapshotBatch([]);
            else previewCapture?.UpdateSnapshotOrder([]);
            status.Text = candidates.Count == 0
                ? "The captured workspace has no eligible windows."
                : "No snapshot targets are available.";
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
                                              HomeDeskRemoteException or HomeDeskProtocolException)
            {
                var candidateIds = ids.ToHashSet();
                foreach (var staleId in previewTargets.Keys.Where(id => !candidateIds.Contains(id)).ToArray())
                    previewTargets.Remove(staleId);
                SyncPreviewCache();
                previewLookupPending = false;
                status.Text = $"Snapshot target lookup failed · {error.Message}";
                if (IsVisible) RenderCards();
                return;
            }
        }

        previewTargets.Clear();
        foreach (var target in liveTargets) previewTargets[target.WindowId] = target;
        previewCapture.SetTargets(liveTargets);
        SyncPreviewCache();
        previewLookupPending = false;
        var captureOrder = GetSnapshotCaptureOrder();
        if (beginSnapshotBatch) previewCapture.BeginSnapshotBatch(captureOrder);
        else previewCapture.UpdateSnapshotOrder(captureOrder);

        status.Text = failed.Count == 0
            ? $"Window snapshots · {previewTargets.Count} targets · Esc cancels"
            : $"Window snapshots · {previewTargets.Count} targets · {failed.Count} unavailable";
        if (IsVisible) RenderCards();
    }

    private void SyncPreviewCache()
    {
        var targetIds = previewTargets.Keys.ToHashSet();
        foreach (var id in previewFrames.Keys.Where(id => !targetIds.Contains(id)).ToArray())
            previewFrames.Remove(id);
        foreach (var id in previewFailures.Keys.Where(id => !targetIds.Contains(id)).ToArray())
            previewFailures.Remove(id);
        sourceSizes.Clear();

        if (previewCapture is null) return;
        foreach (var pair in previewCapture.LatestFrames)
        {
            if (!targetIds.Contains(pair.Key)) continue;
            previewFrames[pair.Key] = pair.Value;
            sourceSizes[pair.Key] = new PreviewImageSize(pair.Value.Width, pair.Value.Height);
        }

        foreach (var pair in previewCapture.LatestFailures)
        {
            if (targetIds.Contains(pair.Key)) previewFailures[pair.Key] = pair.Value;
        }
    }

    private void RenderCards()
    {
        root.Width = Width;
        root.Height = Height;
        shade.Width = Width;
        shade.Height = Height;
        cardsHost.Width = viewportWidth;
        cardsHost.Height = viewportHeight;
        var layouts = CarouselPresentation.Layout(candidates, sourceSizes, workAreaWidth, workAreaHeight,
            viewportWidth, viewportHeight, state.HorizontalOffset, selectedWindowId: state.SelectedWindowId);
        var activeWindowIds = layouts.Select(layout => layout.WindowId).ToHashSet();
        foreach (var staleWindowId in cardVisuals.Keys.Where(windowId => !activeWindowIds.Contains(windowId)).ToArray())
        {
            var staleVisual = cardVisuals[staleWindowId];
            cardsHost.Children.Remove(staleVisual.Button);
            cardVisuals.Remove(staleWindowId);
            staleVisual.Dispose();
        }

        foreach (var layout in layouts)
        {
            var window = candidates.First(candidate => candidate.Id == layout.WindowId);
            var isSelected = layout.WindowId == state.SelectedWindowId;
            previewFrames.TryGetValue(layout.WindowId, out var frame);
            var hasLivePreview = frame is not null;
            var previewMessage = frame is not null ? null : GetPreviewMessage(window, layout.WindowId);
            var title = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
            var key = new CardPresentationKey(title, window.ProcessName, window.Minimized, isSelected,
                hasLivePreview, previewMessage, layout.CardBounds.Width, layout.CardBounds.Height);
            if (!cardVisuals.TryGetValue(layout.WindowId, out var visual) || visual.Key != key)
            {
                if (visual is not null)
                {
                    cardsHost.Children.Remove(visual.Button);
                    visual.Dispose();
                }

                visual = CreateCard(window, layout, isSelected, frame, previewMessage, key);
                cardVisuals[layout.WindowId] = visual;
                cardsHost.Children.Add(visual.Button);
            }
            else
            {
                visual.UpdateFrame(frame);
            }

            var previewBounds = layout.PreviewCardBounds;
            var borderSize = isSelected ? 2 : 0;
            visual.Preview.Width = previewBounds.Width;
            visual.Preview.Height = previewBounds.Height;
            Canvas.SetLeft(visual.Preview, previewBounds.X - borderSize);
            Canvas.SetTop(visual.Preview, previewBounds.Y - borderSize);
            Canvas.SetLeft(visual.Button, layout.CardBounds.X);
            Canvas.SetTop(visual.Button, layout.CardBounds.Y);
        }
    }

    private CardVisual CreateCard(WindowInfo window, CarouselCardLayout layout, bool selected,
        OwnedPreviewFrame? frame, string? previewMessage, CardPresentationKey key)
    {
        var content = BuildCardContent(window, layout, selected, frame, previewMessage, out var preview,
            out var previewBitmap);
        var card = new Button
        {
            Width = layout.CardBounds.Width,
            Height = layout.CardBounds.Height,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = selected ? SwitcherTheme.Accent : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2 : 0),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content
        };
        card.Click += async (_, _) =>
        {
            if (!state.Select(window.Id)) return;
            RenderCards();
            await AcceptSelectionAsync();
        };
        return new CardVisual(window.Id, card, preview, previewBitmap, frame, key);
    }

    private Control BuildCardContent(WindowInfo window, CarouselCardLayout layout, bool selected,
        OwnedPreviewFrame? frame, string? previewMessage, out Border preview, out Bitmap? previewBitmap)
    {
        var borderSize = selected ? 2 : 0;
        var contentWidth = Math.Max(1, layout.CardBounds.Width - borderSize * 2);
        var contentHeight = Math.Max(1, layout.CardBounds.Height - borderSize * 2);
        var panel = new Canvas { Width = contentWidth, Height = contentHeight };
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title,
            Foreground = OverlayText,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(1, contentWidth - 24)
        };
        Canvas.SetLeft(title, 12);
        Canvas.SetTop(title, 8);
        panel.Children.Add(title);
        var processName = OverlayCaption(window.ProcessName);
        Canvas.SetLeft(processName, 12);
        Canvas.SetTop(processName, 30);
        panel.Children.Add(processName);
        var previewBounds = layout.PreviewCardBounds;
        if (frame is not null)
        {
            previewBitmap = CreateAvaloniaBitmap(frame, window.Id);
            preview = CreatePreviewSurface(bitmap: previewBitmap);
        }
        else
        {
            previewBitmap = null;
            var previewCaption = OverlayCaption(previewMessage ?? "Live preview unavailable");
            preview = CreatePreviewSurface(previewCaption);
        }

        preview.Width = previewBounds.Width;
        preview.Height = previewBounds.Height;
        Canvas.SetLeft(preview, previewBounds.X - borderSize);
        Canvas.SetTop(preview, previewBounds.Y - borderSize);
        panel.Children.Add(preview);
        return panel;
    }

    internal static Border CreatePreviewSurface(Control? content = null, Bitmap? bitmap = null) => new()
    {
        Background = bitmap is null ? Brushes.Transparent : new ImageBrush(bitmap) { Stretch = Stretch.Uniform },
        BorderBrush = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(10),
        ClipToBounds = true,
        Child = content
    };

    private static WriteableBitmap CreateAvaloniaBitmap(OwnedPreviewFrame frame, Guid windowId)
    {
        var timer = Stopwatch.StartNew();
        var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = frame.PixelBytes.ToArray();
        var rowLength = checked(frame.Width * 4);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < frame.Height; row++)
            Marshal.Copy(pixels, row * rowLength, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                rowLength);

        timer.Stop();
        PreviewCaptureDiagnostics.Default.RecordUiHandoff(windowId, timer.Elapsed.TotalMilliseconds);
        return bitmap;
    }

    private string GetPreviewMessage(WindowInfo window, Guid windowId)
    {
        if (window.Minimized) return "Snapshot unavailable · Minimized";
        if (!previewTargets.ContainsKey(windowId))
            return previewLookupPending ? "Finding snapshot target…" : "Snapshot unavailable";
        return previewFailures.TryGetValue(windowId, out var failure)
            ? $"Snapshot unavailable · {failure}"
            : "Loading window snapshot…";
    }

    private static TextBlock OverlayCaption(string text)
    {
        var caption = SwitcherTheme.Caption(text);
        caption.Foreground = OverlayMutedText;
        return caption;
    }

    private IReadOnlyList<Guid> GetSnapshotCaptureOrder()
    {
        var layouts = CarouselPresentation.Layout(candidates, sourceSizes, workAreaWidth, workAreaHeight,
            viewportWidth, viewportHeight, state.HorizontalOffset, selectedWindowId: state.SelectedWindowId);
        var visibleTargets = layouts.Where(layout => layout.PreviewBounds.Right > 0 &&
                                                     layout.PreviewBounds.Bottom > 0 &&
                                                     layout.PreviewBounds.Left < viewportWidth &&
                                                     layout.PreviewBounds.Top < viewportHeight)
            .OrderBy(layout => Math.Abs(layout.PreviewBounds.Center.X - viewportWidth / 2))
            .Select(layout => layout.WindowId);
        var selectedIndex = state.SelectedWindowId is { } selected
            ? candidates.ToList().FindIndex(window => window.Id == selected)
            : 0;
        var remainingTargets = candidates.Select((window, index) => (window, index))
            .OrderBy(item => Math.Abs(item.index - selectedIndex))
            .Select(item => item.window.Id);
        return visibleTargets.Concat(remainingTargets).Where(previewTargets.ContainsKey).Distinct().ToArray();
    }

    private void OnPreviewFrameChanged(object? sender, PreviewFrameChangedEventArgs args)
    {
        if (sender is not WindowPreviewCapture capture) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !ReferenceEquals(previewCapture, capture)) return;
            if (args.Frame is null)
            {
                if (args.Failure is null)
                {
                    previewFrames.Remove(args.WindowId);
                    sourceSizes.Remove(args.WindowId);
                    previewFailures.Remove(args.WindowId);
                }
            }
            else
            {
                previewFrames[args.WindowId] = args.Frame;
                sourceSizes[args.WindowId] = new PreviewImageSize(args.Frame.Width, args.Frame.Height);
            }

            if (args.Failure is null) previewFailures.Remove(args.WindowId);
            else previewFailures[args.WindowId] = args.Failure;

            RenderCards();
        }, DispatcherPriority.Render);
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
        StopScrollMotion();
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
        StopScrollMotion();
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
                                          HomeDeskRemoteException or HomeDeskProtocolException)
        {
            state.AcceptanceFailed();
            status.Text = $"Focus failed · {error.Message}";
            return;
        }

        HideCarousel();
    }

    private async Task CancelAsync()
    {
        StopScrollMotion();
        if (state.Cancel() is { } original && managerConnected &&
            snapshot?.Desktop.Windows.Any(window => window.Id == original) == true)
        {
            try
            {
                await focusWindow(original);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                                              HomeDeskRemoteException or HomeDeskProtocolException)
            {
            }
        }

        HideCarousel();
    }

    private void HideCarousel()
    {
        StopScrollMotion();
        state.CloseExternally();
        CancelPreviewLookup();
        previewCapture?.Pause();
        ClearPreviewPresentation();
        if (IsVisible) Hide();
    }

    private void OnScrollAnimationTick(object? sender, EventArgs args)
    {
        if (!IsVisible || state.Lifecycle != CarouselLifecycleState.Open)
        {
            StopScrollMotion();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(lastScrollTick, now).TotalSeconds;
        lastScrollTick = now;
        var delta = scrollMotion.Advance(elapsed);
        if (delta != 0)
        {
            var maximumOffset = CarouselPresentation.MaximumScrollOffset(candidates.Count, workAreaWidth,
                viewportWidth);
            if (!state.ScrollBy(delta, maximumOffset, CarouselPresentation.CardStride(workAreaWidth),
                    CarouselPresentation.CardWidth(workAreaWidth), viewportWidth))
            {
                StopScrollMotion();
                return;
            }

            RenderCards();
        }

        if (!scrollMotion.IsActive) scrollAnimationTimer.Stop();
    }

    private void StopScrollMotion()
    {
        scrollAnimationTimer.Stop();
        scrollMotion.Stop();
        lastScrollTick = 0;
    }

    private void CancelPreviewLookup()
    {
        if (openCancellation is not { } cancellation) return;
        openCancellation = null;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void StartPreviewCapture()
    {
        if (previewCapture is null)
        {
            previewCapture = new WindowPreviewCapture();
            previewCapture.FrameChanged += OnPreviewFrameChanged;
        }

        previewCapture.Pause();
        ClearPreviewPresentation();
    }

    private void ReleasePreviewCapture()
    {
        if (previewCapture is { } capture)
        {
            previewCapture = null;
            capture.FrameChanged -= OnPreviewFrameChanged;
            capture.Dispose();
        }

        ClearPreviewPresentation();
    }

    private void ClearPreviewPresentation()
    {
        foreach (var visual in cardVisuals.Values)
        {
            cardsHost.Children.Remove(visual.Button);
            visual.Dispose();
        }

        cardVisuals.Clear();
        previewTargets.Clear();
        previewFrames.Clear();
        previewFailures.Clear();
        sourceSizes.Clear();
        previewLookupPending = false;
    }

    private readonly record struct CardPresentationKey(
        string Title,
        string ProcessName,
        bool Minimized,
        bool Selected,
        bool HasLivePreview,
        string? PreviewMessage,
        double Width,
        double Height);

    private sealed class CardVisual(
        Guid windowId,
        Button button,
        Border preview,
        Bitmap? previewBitmap,
        OwnedPreviewFrame? frame,
        CardPresentationKey key) : IDisposable
    {
        public Button Button { get; } = button;
        public Border Preview { get; } = preview;
        public CardPresentationKey Key { get; } = key;
        private OwnedPreviewFrame? CurrentFrame { get; set; } = frame;
        private Bitmap? PreviewBitmap { get; set; } = previewBitmap;

        public void UpdateFrame(OwnedPreviewFrame? nextFrame)
        {
            if (ReferenceEquals(CurrentFrame, nextFrame)) return;
            var previous = PreviewBitmap;
            PreviewBitmap = nextFrame is null ? null : CreateAvaloniaBitmap(nextFrame, windowId);
            Preview.Background = PreviewBitmap is null
                ? Brushes.Transparent
                : new ImageBrush(PreviewBitmap) { Stretch = Stretch.Uniform };
            CurrentFrame = nextFrame;
            previous?.Dispose();
        }

        public void Dispose()
        {
            var bitmap = PreviewBitmap;
            PreviewBitmap = null;
            Preview.Background = Brushes.Transparent;
            bitmap?.Dispose();
        }
    }
}