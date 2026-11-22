using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using HomeDesk.Contracts;
using HomeDeskManager;
using Xunit;

namespace HomeDesk.Ipc.Tests;

public sealed class WorkspaceSpreadPreviewTests
{
    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(true, null, false, false)]
    [InlineData(false, 1, false, false)]
    [InlineData(true, 1, true, false)]
    [InlineData(true, 1, false, true)]
    public void SpreadRequiresCtrlHoverAndNoOpenContextMenu(bool controlHeld, int? hasHoveredWorkspace,
        bool contextMenuOpen, bool expected)
    {
        Assert.Equal(expected, WorkspaceSpreadPreview.ShouldShow(controlHeld,
            hasHoveredWorkspace is null ? null : Guid.NewGuid(), contextMenuOpen));
    }

    [Fact]
    public void LayoutClampsAtDeskEdgesAndCapsSpreadWidth()
    {
        var layout = WorkspaceSpreadPreview.CalculateLayout(new(460, 300), 9, new(500, 320));

        Assert.Equal(2, layout.Columns);
        Assert.Equal(new Point(104, 12), layout.Position);
        Assert.Equal(new Size(384, 296), layout.Size);
        Assert.InRange(layout.Position.X, 12, 500 - layout.Size.Width - 12);
        Assert.InRange(layout.Position.Y, 12, 320 - layout.Size.Height - 12);
    }

    [Fact]
    public void OfflineSpreadShowsOneCompactCardForEachWorkspaceWindowAndUnavailablePlaceholders()
    {
        var visible = Window("Visible");
        var minimized = Window("Minimized", minimized: true);
        var managerHidden = Window("Manager hidden") with { ManagerHidden = true };
        var ignored = Window("Ignored") with { Fullscreen = true };
        var workspace = Workspace(visible.Id, minimized.Id, managerHidden.Id, ignored.Id);
        var preview = new WorkspaceSpreadPreview((_, _) => Task.FromResult(EmptyTargets()));

        preview.ShowForWorkspace(workspace, [visible, minimized, managerHidden, ignored], false);

        Assert.True(preview.IsOpen);
        Assert.Equal(workspace.Id, preview.WorkspaceId);
        Assert.Equal(2, preview.CardCount);
        Assert.Equal(["Connect to preview this window", "Connect to preview this window"], preview.PreviewMessages);
        Assert.All(preview.PreviewCards, card =>
        {
            Assert.Equal(WorkspaceSpreadPreview.PreviewCardWidth, card.Width);
            Assert.Equal(WorkspaceSpreadPreview.PreviewCardHeight, card.Height);
        });
        Assert.False(preview.IsEmptyStateVisible);

        preview.Dismiss();
        Assert.False(preview.IsOpen);
        Assert.Equal(0, preview.CardCount);
    }

    [Fact]
    public void EmptyWorkspaceShowsAnEmptyStateInsteadOfWindowCards()
    {
        var preview = new WorkspaceSpreadPreview((_, _) => Task.FromResult(EmptyTargets()));
        preview.ShowForWorkspace(Workspace(), [], false);

        Assert.True(preview.IsEmptyStateVisible);
        Assert.Equal(0, preview.CardCount);
    }

    [Fact]
    public void ClickingCurrentWindowPreviewRequestsActivation()
    {
        var window = Window("Editor");
        var preview = new WorkspaceSpreadPreview((_, _) => Task.FromResult(EmptyTargets()));
        Guid? requestedWindowId = null;
        preview.WindowActivationRequested += windowId => requestedWindowId = windowId;
        preview.ShowForWorkspace(Workspace(window.Id), [window], false);

        Assert.Single(preview.PreviewCards).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(window.Id, requestedWindowId);
    }

    [Fact]
    public void SpreadAnimatesFromWorkspaceAndDismissalCollapsesWithoutAllowingActivation()
    {
        var window = Window("Editor");
        var workspace = Workspace(window.Id) with { Position = new DeskPosition(200, 150) };
        var preview = new WorkspaceSpreadPreview((_, _) => Task.FromResult(EmptyTargets()));
        Guid? requestedWindowId = null;
        preview.WindowActivationRequested += windowId => requestedWindowId = windowId;

        preview.ShowForWorkspace(workspace, [window], false);
        preview.SetPlacement(new Point(200, 150), new Size(800, 600));

        var layout = WorkspaceSpreadPreview.CalculateLayout(new Point(200, 150), 1, new Size(800, 600));
        Assert.Equal(1, preview.AnimationTargetProgress);
        Assert.Equal(new RelativePoint(
            (200 + 141 / 2d - layout.Position.X) / layout.Size.Width,
            (150 + 112 / 2d - layout.Position.Y) / layout.Size.Height,
            RelativeUnit.Relative), preview.RenderTransformOrigin);
        preview.SetAnimationProgressForTest(0.5);
        Assert.Equal(0.5, preview.Opacity);
        Assert.Equal(0.54, Assert.IsType<ScaleTransform>(preview.RenderTransform).ScaleX, 2);

        var card = Assert.Single(preview.PreviewCards);
        preview.Dismiss();
        Assert.False(preview.IsOpen);
        Assert.True(preview.IsVisible);
        Assert.Single(preview.PreviewCards);
        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(requestedWindowId);

        preview.SetAnimationProgressForTest(0);
        Assert.False(preview.IsVisible);
        Assert.Equal(0, preview.CardCount);
        Assert.Equal(0, preview.Opacity);
    }

    [Fact]
    public void ReopeningSameWorkspaceReversesCollapseAndKeepsCards()
    {
        var window = Window("Editor");
        var workspace = Workspace(window.Id);
        var preview = new WorkspaceSpreadPreview((_, _) => Task.FromResult(EmptyTargets()));
        preview.ShowForWorkspace(workspace, [window], false);
        preview.SetPlacement(new Point(0, 0), new Size(800, 600));
        preview.SetAnimationProgressForTest(0.4);
        var card = Assert.Single(preview.PreviewCards);

        preview.Dismiss();
        Assert.Equal(0, preview.AnimationTargetProgress);
        preview.ShowForWorkspace(workspace, [window], false);
        preview.SetPlacement(new Point(0, 0), new Size(800, 600));

        Assert.True(preview.IsOpen);
        Assert.Equal(1, preview.AnimationTargetProgress);
        Assert.Same(card, Assert.Single(preview.PreviewCards));
        Assert.Equal(0.4, preview.AnimationProgress, 2);
        preview.ResetSession();
    }

    [Fact]
    public void RemovedWindowPreviewCannotActivateAndUnchangedCardsStayStable()
    {
        var removedWindow = Window("Removed");
        var currentWindow = Window("Current");
        var workspace = Workspace(removedWindow.Id, currentWindow.Id);
        var preview = new WorkspaceSpreadPreview((_, _) => Task.FromResult(EmptyTargets()));
        Guid? requestedWindowId = null;
        preview.WindowActivationRequested += windowId => requestedWindowId = windowId;
        preview.ShowForWorkspace(workspace, [removedWindow, currentWindow], false);
        var removedCard = preview.PreviewCards[0];
        var currentCard = preview.PreviewCards[1];

        preview.UpdateWorkspace(workspace with { WindowIds = [currentWindow.Id] },
            [removedWindow, currentWindow], false);
        removedCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(requestedWindowId);
        Assert.Same(currentCard, Assert.Single(preview.PreviewCards));
        currentCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(currentWindow.Id, requestedWindowId);
    }

    [Fact]
    public async Task DismissedSpreadKeepsPendingLookupAndReopeningReusesItsResult()
    {
        var result = new TaskCompletionSource<PreviewTargetsResult>();
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = Window("Editor");
        var lookupCount = 0;
        CancellationToken lookupToken = default;
        WindowPreviewCapture? capture = null;
        var preview = new WorkspaceSpreadPreview((_, token) =>
        {
            Interlocked.Increment(ref lookupCount);
            lookupToken = token;
            lookupStarted.SetResult();
            return result.Task;
        }, () =>
        {
            var createdCapture = CreateTestCapture();
            capture = createdCapture;
            return createdCapture;
        });
        preview.ShowForWorkspace(Workspace(window.Id), [window], true);
        Assert.True(lookupStarted.Task.IsCompleted);
        var lookup = preview.PendingLookup!;

        try
        {
            preview.Dismiss();
            Assert.False(lookupToken.IsCancellationRequested);
            result.SetResult(new PreviewTargetsResult(1,
                [new WindowPreviewTarget(window.Id, 1, 1, 1)], []));
            await lookup.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(preview.IsOpen);
            Assert.Contains(window.Id, preview.PreviewTargetIds);
            preview.ShowForWorkspace(Workspace(window.Id), [window], true);

            Assert.Equal(1, lookupCount);
            Assert.Contains(window.Id, preview.PreviewTargetIds);
            Assert.Single(preview.PreviewCards);
        }
        finally
        {
            capture?.Dispose();
        }
    }

    [Fact]
    public async Task ResetSessionCancelsPendingLookupAndRejectsLateResults()
    {
        var result = new TaskCompletionSource<PreviewTargetsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lookupToken = default;
        var window = Window("Editor");
        var preview = new WorkspaceSpreadPreview((_, token) =>
        {
            lookupToken = token;
            lookupStarted.SetResult();
            return result.Task;
        }, CreateTestCapture);
        preview.ShowForWorkspace(Workspace(window.Id), [window], true);
        Assert.True(lookupStarted.Task.IsCompleted);
        var lookup = preview.PendingLookup!;

        preview.ResetSession();
        result.SetResult(new PreviewTargetsResult(1,
            [new WindowPreviewTarget(window.Id, 1, 1, 1)], []));
        await lookup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(lookupToken.IsCancellationRequested);
        Assert.Empty(preview.PreviewTargetIds);
        Assert.False(preview.IsOpen);
    }

    [Fact]
    public void SwitchingWorkspacesQueriesOnlyPreviouslyUnseenWindowIds()
    {
        var firstWindow = Window("First");
        var secondWindow = Window("Second");
        var requestedIds = new List<Guid[]>();
        var preview = new WorkspaceSpreadPreview((ids, _) =>
        {
            requestedIds.Add(ids.ToArray());
            return Task.FromResult(new PreviewTargetsResult(1,
                ids.Select(id => new WindowPreviewTarget(id, 1, 1, 1)).ToArray(), []));
        }, CreateTestCapture);

        try
        {
            preview.ShowForWorkspace(Workspace(firstWindow.Id), [firstWindow], true);
            preview.UpdateWorkspace(Workspace(firstWindow.Id, secondWindow.Id), [firstWindow, secondWindow], true);

            Assert.Equal(2, requestedIds.Count);
            Assert.Equal([firstWindow.Id], requestedIds[0]);
            Assert.Equal([secondWindow.Id], requestedIds[1]);
            Assert.Contains(firstWindow.Id, preview.PreviewTargetIds);
            Assert.Contains(secondWindow.Id, preview.PreviewTargetIds);
        }
        finally
        {
            preview.ResetSession();
        }
    }

    [Fact]
    public void ReopeningSpreadDoesNotRestartWindowCaptureBatch()
    {
        var window = Window("Editor");
        var queryCount = 0;
        var captureCount = 0;
        var captureCreatedCount = 0;
        var captureStarted = new ManualResetEventSlim();
        var capturedFrame =
            new TaskCompletionSource<OwnedPreviewFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captureToken = default;
        var preview = new WorkspaceSpreadPreview((ids, _) =>
        {
            Interlocked.Increment(ref queryCount);
            return Task.FromResult(new PreviewTargetsResult(1,
                ids.Select(id => new WindowPreviewTarget(id, 1, 1, 1)).ToArray(), []));
        }, () =>
        {
            Interlocked.Increment(ref captureCreatedCount);
            return new WindowPreviewCapture((_, token) =>
            {
                Interlocked.Increment(ref captureCount);
                captureToken = token;
                captureStarted.Set();
                return capturedFrame.Task;
            }, 1);
        });

        try
        {
            preview.ShowForWorkspace(Workspace(window.Id), [window], true);
            Assert.True(captureStarted.Wait(TimeSpan.FromSeconds(2)));
            preview.Dismiss();
            Assert.False(captureToken.IsCancellationRequested);
            preview.ShowForWorkspace(Workspace(window.Id), [window], true);

            Assert.Equal(1, queryCount);
            Assert.Equal(1, captureCreatedCount);
            Assert.Equal(1, captureCount);
        }
        finally
        {
            capturedFrame.TrySetResult(null);
            preview.ResetSession();
            captureStarted.Dispose();
        }
    }

    private static WorkspaceInfo Workspace(params Guid[] windowIds) =>
        new(Guid.NewGuid(), "Code", Guid.NewGuid(), false, windowIds, WorkspaceLayout.Split, new DeskPosition(0, 0));

    private static WindowInfo Window(string title, bool minimized = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, "process", "window", new(0, 0, 300, 200), true,
            minimized, false, WindowMode.Tiling);

    private static PreviewTargetsResult EmptyTargets() => new(1, [], []);

    private static WindowPreviewCapture CreateTestCapture() =>
        new((_, _) => Task.FromResult<OwnedPreviewFrame?>(null), 1);
}