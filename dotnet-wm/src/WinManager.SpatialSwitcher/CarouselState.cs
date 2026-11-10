using WinManager.Contracts;

namespace WinManager.SpatialSwitcher;

internal enum CarouselLifecycleState
{
    Closed,
    Opening,
    Open,
    Closing
}

internal sealed class CarouselState
{
    private readonly HashSet<Guid> candidates = [];
    private readonly List<Guid> candidateOrder = [];

    public CarouselLifecycleState Lifecycle { get; private set; }
    public SwitcherContext? Context { get; private set; }
    public Guid? OriginalWindowId { get; private set; }
    public Guid? SelectedWindowId { get; private set; }
    public double HorizontalOffset { get; private set; }

    public void BeginOpen(SwitcherContext context, IReadOnlyList<WindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (Lifecycle != CarouselLifecycleState.Closed)
            throw new InvalidOperationException("The carousel must be closed before opening a new context.");
        Lifecycle = CarouselLifecycleState.Opening;
        Context = context;
        candidates.Clear();
        candidateOrder.Clear();
        foreach (var window in windows)
            if (candidates.Add(window.Id))
                candidateOrder.Add(window.Id);
        OriginalWindowId = context.WindowId;
        SelectedWindowId = context.WindowId is { } selected && candidates.Contains(selected)
            ? selected
            : windows.FirstOrDefault()?.Id;
        HorizontalOffset = 0;
    }

    public bool MarkOpen()
    {
        if (Lifecycle != CarouselLifecycleState.Opening) return false;
        Lifecycle = CarouselLifecycleState.Open;
        return true;
    }

    public bool Select(Guid windowId)
    {
        if (Lifecycle != CarouselLifecycleState.Open || !candidates.Contains(windowId)) return false;
        SelectedWindowId = windowId;
        return true;
    }

    public void UpdateCandidates(IReadOnlyList<WindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (Lifecycle != CarouselLifecycleState.Open) return;
        var previousSelection = SelectedWindowId;
        candidates.Clear();
        candidateOrder.Clear();
        foreach (var window in windows)
            if (candidates.Add(window.Id))
                candidateOrder.Add(window.Id);
        SelectedWindowId = previousSelection is { } selected && candidates.Contains(selected)
            ? selected
            : OriginalWindowId is { } original && candidates.Contains(original)
                ? original
                : candidateOrder.FirstOrDefault() is { } first
                    ? first
                    : null;
    }

    public void ClampOffset(double maximumOffset) =>
        HorizontalOffset = Math.Clamp(HorizontalOffset, 0, Math.Max(0, maximumOffset));

    public void SetInitialOffset(double offset, double maximumOffset)
    {
        if (Lifecycle != CarouselLifecycleState.Opening) return;
        HorizontalOffset = Math.Clamp(offset, 0, Math.Max(0, maximumOffset));
    }

    public bool ScrollBy(double delta, double maximumOffset, double cardStride, double cardWidth,
        double viewportWidth)
    {
        if (Lifecycle != CarouselLifecycleState.Open || candidateOrder.Count == 0 || !double.IsFinite(delta) ||
            !double.IsFinite(maximumOffset) || !double.IsFinite(cardStride) || cardStride <= 0 || cardWidth <= 0 ||
            viewportWidth <= 0) return false;
        var nextOffset = Math.Clamp(HorizontalOffset + delta, 0, Math.Max(0, maximumOffset));
        if (Math.Abs(nextOffset - HorizontalOffset) < 0.001) return false;
        HorizontalOffset = nextOffset;
        var selectionAnchor = nextOffset + viewportWidth / 2;
        var nearest = (int)Math.Round((selectionAnchor - cardWidth / 2) / cardStride,
            MidpointRounding.AwayFromZero);
        nearest = Math.Clamp(nearest, 0, candidateOrder.Count - 1);
        SelectedWindowId = candidateOrder[nearest];
        return true;
    }

    public void ScrollToSelected(double cardStride, double maximumOffset)
    {
        if (Lifecycle != CarouselLifecycleState.Open || SelectedWindowId is not { } selected) return;
        var index = candidateOrder.IndexOf(selected);
        if (index >= 0) HorizontalOffset = Math.Clamp(index * cardStride, 0, Math.Max(0, maximumOffset));
    }

    public ManagerCommand? Accept()
    {
        if (Lifecycle != CarouselLifecycleState.Open || SelectedWindowId is not { } selected) return null;
        Lifecycle = CarouselLifecycleState.Closing;
        return SpatialSwitcherPresentation.FocusCommand(selected);
    }

    public Guid? Cancel()
    {
        if (Lifecycle is not (CarouselLifecycleState.Open or CarouselLifecycleState.Opening)) return null;
        Lifecycle = CarouselLifecycleState.Closing;
        return OriginalWindowId is { } original && candidates.Contains(original) ? original : null;
    }

    public void AcceptanceFailed()
    {
        if (Lifecycle == CarouselLifecycleState.Closing) Lifecycle = CarouselLifecycleState.Open;
    }

    public void CloseExternally()
    {
        if (Lifecycle != CarouselLifecycleState.Closed) Lifecycle = CarouselLifecycleState.Closing;
        CompleteClose();
    }

    public void CompleteClose()
    {
        Lifecycle = CarouselLifecycleState.Closed;
        Context = null;
        OriginalWindowId = null;
        SelectedWindowId = null;
        HorizontalOffset = 0;
        candidates.Clear();
        candidateOrder.Clear();
    }
}