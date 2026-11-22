using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HomeDesk.Contracts;

namespace HomeDeskManager;

internal sealed class WorkspaceSpreadPreview : Border
{
    internal const double PreviewCardWidth = 176;
    internal const double PreviewCardHeight = 132;

    private const double CardSpacing = 12;
    private const double OverlayPadding = 10;
    private readonly Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets;
    private readonly Func<WindowPreviewCapture> createPreviewCapture;
    private readonly WrapPanel cardsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer scrollViewer = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };
    private readonly TextBlock heading = new();
    private readonly TextBlock emptyState = new()
    {
        Text = "No windows in this workspace",
        Foreground = SwitcherTheme.MutedText,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Dictionary<Guid, WindowPreviewTarget> previewTargets = [];
    private readonly Dictionary<Guid, OwnedPreviewFrame> previewFrames = [];
    private readonly Dictionary<Guid, string> previewFailures = [];
    private readonly Dictionary<Guid, SpreadWindowCard> cards = [];
    private readonly HashSet<Guid> lookupStartedIds = [];
    private readonly HashSet<Guid> lookupPendingIds = [];
    private WindowInfo[] windows = [];
    private Guid[] renderedCardIds = [];
    private WindowPreviewCapture? previewCapture;
    private CancellationTokenSource? previewSessionCancellation;
    private Task? previewLookupTask;
    private Guid? workspaceId;
    private string workspaceName = string.Empty;
    private bool previewAvailable;
    private bool captureBatchStarted;
    private bool isOpen;

    public WorkspaceSpreadPreview(
        Func<IReadOnlyList<Guid>, CancellationToken, Task<PreviewTargetsResult>> queryPreviewTargets,
        Func<WindowPreviewCapture>? createPreviewCapture = null)
    {
        this.queryPreviewTargets = queryPreviewTargets;
        this.createPreviewCapture = createPreviewCapture ?? (() => new WindowPreviewCapture());

        Background = SwitcherTheme.Surface;
        BorderBrush = SwitcherTheme.Accent;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(OverlayPadding);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsVisible = false;
        ClipToBounds = true;

        heading.FontSize = 13;
        heading.FontWeight = FontWeight.SemiBold;
        heading.Foreground = SwitcherTheme.Text;
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        scrollViewer.Content = cardsPanel;

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions { new(GridLength.Auto), new(GridLength.Star) },
            RowSpacing = 8
        };
        content.Children.Add(heading);
        var body = new Grid();
        body.Children.Add(scrollViewer);
        body.Children.Add(emptyState);
        Grid.SetRow(body, 1);
        content.Children.Add(body);
        Child = content;
    }

    internal bool IsOpen => isOpen;
    internal Guid? WorkspaceId => workspaceId;
    internal int CardCount => cards.Count;
    internal IReadOnlyCollection<Guid> PreviewTargetIds => previewTargets.Keys;
    internal bool IsEmptyStateVisible => emptyState.IsVisible;
    internal IReadOnlyList<Button> PreviewCards => windows.Select(window => cards[window.Id].Card).ToArray();
    internal IReadOnlyList<string?> PreviewMessages => windows.Select(window => cards[window.Id].Message).ToArray();
    internal Task? PendingLookup => previewLookupTask;
    internal event Action<Guid>? WindowActivationRequested;

    internal static bool ShouldShow(bool controlHeld, Guid? hoveredWorkspaceId, bool contextMenuOpen) =>
        controlHeld && hoveredWorkspaceId is not null && !contextMenuOpen;

    internal static IReadOnlyList<WindowInfo> WindowsForWorkspace(WorkspaceInfo? workspace,
        IReadOnlyList<WindowInfo> windows) => SpatialSwitcherPresentation.WindowsForWorkspace(workspace, windows);

    internal static WorkspaceSpreadLayout CalculateLayout(Point workspacePosition, int windowCount, Size viewport)
    {
        var margin = 12d;
        var availableWidth = Math.Max(0, viewport.Width - margin * 2);
        var availableHeight = Math.Max(0, viewport.Height - margin * 2);
        var maximumColumns = Math.Clamp((int)Math.Floor((availableWidth + CardSpacing) /
                                                       (PreviewCardWidth + CardSpacing)), 1, 3);
        var columns = windowCount == 0 ? 1 : Math.Min(windowCount, maximumColumns);
        var rows = windowCount == 0 ? 0 : (windowCount + columns - 1) / columns;
        var cardsWidth = columns * PreviewCardWidth + (columns - 1) * CardSpacing;
        var width = Math.Min(availableWidth, cardsWidth + OverlayPadding * 2);
        var naturalHeight = windowCount == 0
            ? 92
            : 46 + rows * PreviewCardHeight + Math.Max(0, rows - 1) * CardSpacing;
        var height = Math.Min(availableHeight, naturalHeight);
        var left = workspacePosition.X + 141 / 2d - width / 2;
        var top = workspacePosition.Y + 112 / 2d - height / 2;
        left = Math.Clamp(left, margin, Math.Max(margin, viewport.Width - width - margin));
        top = Math.Clamp(top, margin, Math.Max(margin, viewport.Height - height - margin));
        return new WorkspaceSpreadLayout(new Point(left, top), new Size(width, height), columns);
    }

    internal void ShowForWorkspace(WorkspaceInfo workspace, IReadOnlyList<WindowInfo> workspaceWindows,
        bool canPreview)
    {
        isOpen = true;
        IsVisible = true;
        UpdateWorkspace(workspace, workspaceWindows, canPreview);
    }

    internal void UpdateWorkspace(WorkspaceInfo? workspace, IReadOnlyList<WindowInfo> workspaceWindows,
        bool canPreview)
    {
        if (!isOpen) return;
        if (workspace is null)
        {
            Dismiss();
            return;
        }

        workspaceId = workspace.Id;
        workspaceName = workspace.Name;
        windows = WindowsForWorkspace(workspace, workspaceWindows).ToArray();
        heading.Text = $"{workspaceName} · {windows.Length} windows";
        previewAvailable = canPreview;

        var candidates = canPreview
            ? windows.Where(window => window.Visible && !window.Minimized).Select(window => window.Id).Distinct().ToArray()
            : [];
        if (canPreview) RequestPreviewTargets(candidates);

        RenderCards();
    }

    internal void SetPlacement(Point workspacePosition, Size viewport)
    {
        if (!isOpen) return;
        var layout = CalculateLayout(workspacePosition, windows.Length, viewport);
        Width = layout.Size.Width;
        Height = layout.Size.Height;
        cardsPanel.Width = Math.Max(1, layout.Size.Width - OverlayPadding * 2);
        Canvas.SetLeft(this, layout.Position.X);
        Canvas.SetTop(this, layout.Position.Y);
    }

    internal void Dismiss()
    {
        isOpen = false;
        IsVisible = false;
        workspaceId = null;
        workspaceName = string.Empty;
        windows = [];
        foreach (var card in cards.Values) card.Dispose();
        cards.Clear();
        cardsPanel.Children.Clear();
        renderedCardIds = [];
    }

    internal void ResetSession()
    {
        Dismiss();
        if (previewSessionCancellation is { } cancellation)
        {
            previewSessionCancellation = null;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        if (previewCapture is { } capture)
        {
            previewCapture = null;
            capture.FrameChanged -= OnPreviewFrameChanged;
            capture.Dispose();
        }

        previewTargets.Clear();
        previewFrames.Clear();
        previewFailures.Clear();
        lookupStartedIds.Clear();
        lookupPendingIds.Clear();
        previewLookupTask = null;
        captureBatchStarted = false;
    }

    private void RequestPreviewTargets(Guid[] candidates)
    {
        if (candidates.Length == 0) return;

        EnsurePreviewCapture();
        if (!captureBatchStarted)
        {
            previewCapture!.BeginSnapshotBatch([]);
            captureBatchStarted = true;
        }

        previewCapture!.SetTargets(previewTargets.Values);
        previewCapture.UpdateSnapshotOrder(previewTargets.Keys);
        SyncPreviewCache();

        var ids = candidates.Where(lookupStartedIds.Add).ToArray();
        if (ids.Length == 0) return;

        lookupPendingIds.UnionWith(ids);
        var cancellation = previewSessionCancellation ??= new CancellationTokenSource();
        previewLookupTask = BindPreviewTargetsAsync(ids, cancellation);
    }

    private async Task BindPreviewTargetsAsync(IReadOnlyList<Guid> ids, CancellationTokenSource cancellation)
    {
        try
        {
            var targets = new List<WindowPreviewTarget>();
            var failures = new List<PreviewTargetFailure>();
            foreach (var chunk in ids.Chunk(PreviewTargetsRequestRules.MaximumWindowIds))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var result = await queryPreviewTargets(chunk, cancellation.Token);
                targets.AddRange(result.Targets.Where(target => chunk.Contains(target.WindowId)));
                failures.AddRange(result.Failures.Where(failure => chunk.Contains(failure.WindowId)));
            }

            if (!IsCurrentPreviewSession(cancellation)) return;
            foreach (var target in targets) previewTargets[target.WindowId] = target;
            foreach (var failure in failures) previewFailures[failure.WindowId] = failure.Code;
            foreach (var id in ids) lookupPendingIds.Remove(id);
            previewCapture?.SetTargets(previewTargets.Values);
            if (captureBatchStarted) previewCapture?.UpdateSnapshotOrder(previewTargets.Keys);
            SyncPreviewCache();
            if (isOpen) RenderCards();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsCurrentPreviewSession(cancellation)) return;
            foreach (var id in ids)
            {
                previewFailures[id] = error.Message;
                lookupPendingIds.Remove(id);
            }

            if (isOpen) RenderCards();
        }
        finally
        {
            if (ReferenceEquals(previewSessionCancellation, cancellation))
                foreach (var id in ids) lookupPendingIds.Remove(id);
        }
    }

    private bool IsCurrentPreviewSession(CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested && ReferenceEquals(previewSessionCancellation, cancellation);

    private void EnsurePreviewCapture()
    {
        if (previewCapture is not null) return;
        previewCapture = createPreviewCapture();
        previewCapture.FrameChanged += OnPreviewFrameChanged;
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
            if (!ReferenceEquals(previewCapture, capture) || !previewTargets.ContainsKey(args.WindowId))
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
            if (isOpen && windows.Any(window => window.Id == args.WindowId)) RenderCards();
        }, DispatcherPriority.Render);
    }

    private void RenderCards()
    {
        foreach (var window in windows)
        {
            if (!cards.TryGetValue(window.Id, out var card))
            {
                card = new SpreadWindowCard();
                var windowId = window.Id;
                card.Card.Click += (_, _) => RequestWindowActivation(windowId);
                cards.Add(window.Id, card);
            }

            card.Update(window, previewFrames.GetValueOrDefault(window.Id), PreviewMessage(window));
        }

        var visibleIds = windows.Select(window => window.Id).ToHashSet();
        foreach (var id in cards.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
        {
            cards[id].Dispose();
            cards.Remove(id);
        }

        var cardIds = windows.Select(window => window.Id).ToArray();
        if (!cardIds.SequenceEqual(renderedCardIds))
        {
            cardsPanel.Children.Clear();
            foreach (var window in windows) cardsPanel.Children.Add(cards[window.Id].Card);
            renderedCardIds = cardIds;
        }
        heading.Text = $"{workspaceName} · {windows.Length} windows";
        emptyState.IsVisible = windows.Length == 0;
        scrollViewer.IsVisible = windows.Length > 0;
    }

    private void RequestWindowActivation(Guid windowId)
    {
        if (!isOpen || !cards.ContainsKey(windowId) || windows.All(window => window.Id != windowId)) return;
        WindowActivationRequested?.Invoke(windowId);
    }

    private string PreviewMessage(WindowInfo window)
    {
        if (!previewAvailable) return "Connect to preview this window";
        if (!window.Visible || window.Minimized) return "Window hidden or minimized";
        if (previewFailures.TryGetValue(window.Id, out var failure)) return $"Preview unavailable · {failure}";
        if (!previewTargets.ContainsKey(window.Id))
            return lookupPendingIds.Contains(window.Id) ? "Finding preview…" : "Preview unavailable";
        return "Loading preview…";
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

    private sealed class SpreadWindowCard : IDisposable
    {
        private readonly Image preview = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock title = new()
        {
            FontSize = 11,
            Foreground = SwitcherTheme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock placeholder = new()
        {
            FontSize = 10,
            Foreground = SwitcherTheme.MutedText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8)
        };
        private WriteableBitmap? bitmap;
        private OwnedPreviewFrame? currentFrame;

        public SpreadWindowCard()
        {
            var previewPanel = new Grid
            {
                Height = 90,
                ClipToBounds = true,
                Background = SwitcherTheme.Canvas
            };
            previewPanel.Children.Add(preview);
            previewPanel.Children.Add(placeholder);
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(previewPanel);
            content.Children.Add(title);
            Card = new Button
            {
                Width = PreviewCardWidth,
                Height = PreviewCardHeight,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new Border
                {
                    Padding = new Thickness(7),
                    Background = SwitcherTheme.Canvas,
                    BorderBrush = SwitcherTheme.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Child = content
                }
            };
        }

        public Button Card { get; }
        public string? Message => placeholder.Text;

        public void Update(WindowInfo window, OwnedPreviewFrame? frame, string message)
        {
            title.Text = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
            if (frame is not null && !ReferenceEquals(frame, currentFrame))
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
                placeholder.Text = message;
            }
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

internal readonly record struct WorkspaceSpreadLayout(Point Position, Size Size, int Columns);
