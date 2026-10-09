using System.Diagnostics;
using System.Runtime.Versioning;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Windows.Tests;

[Collection("Isolated interactive desktop")]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowsWindowPlatformInteractiveTests
{
    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task MoveEventsProtectGeometryUntilDropLayoutIsAcknowledged()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var first = Find(await observer.CaptureAsync(timeout.Token), fixture);
        await using var notifications = observer.Notifications(timeout.Token).GetAsyncEnumerator();
        await fixture.CommandAsync("move-start");
        await WaitForMoveAsync(notifications, NativeNotificationKind.MoveStarted, first.Id);
        await fixture.CommandAsync("move");
        var moved = Find(await observer.CaptureAsync(timeout.Token), fixture);
        Assert.NotEqual(first.Bounds, moved.Bounds);
        var stale = new LayoutPlan([new(first.Id, first.Bounds)]);
        Assert.Empty((await observer.ApplyAsync(stale, timeout.Token)).Failures);
        Assert.Equal(moved.Bounds, Find(await observer.CaptureAsync(timeout.Token), fixture).Bounds);
        await fixture.CommandAsync("move-end");
        await WaitForMoveAsync(notifications, NativeNotificationKind.MoveEnded, first.Id);
        Assert.Empty((await observer.ApplyAsync(stale, timeout.Token)).Failures);
        Assert.Equal(moved.Bounds, Find(await observer.CaptureAsync(timeout.Token), fixture).Bounds);
        Assert.Empty((await observer.ApplyAsync(stale with { CompletedMoves = [first.Id] }, timeout.Token)).Failures);
        await WaitForBoundsAsync(observer, fixture, first.Bounds, timeout.Token);
    }

    private static async Task WaitForMoveAsync(IAsyncEnumerator<NativeNotification> notifications,
        NativeNotificationKind kind, Guid windowId)
    {
        while (await notifications.MoveNextAsync())
            if (notifications.Current.Kind == kind && notifications.Current.WindowId == windowId) return;
        Assert.Fail("Desktop notifications ended before the expected move event.");
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task AppliesVisibleGeometryWithoutActivationOrZOrderAndClampsApplicationMinimum()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var first = Find(await observer.CaptureAsync(timeout.Token), fixture);
        Assert.InRange(first.MinimumWidth, 300, 340);
        Assert.InRange(first.MinimumHeight, 200, 240);
        var state = NativeInspection.WindowState(first.Title);
        var target = new PixelRect(first.Bounds.X + 45, first.Bounds.Y + 55, 620, 410);
        Assert.Empty((await observer.ApplyAsync(new LayoutPlan([new(first.Id, target)]), timeout.Token)).Failures);
        await WaitForBoundsAsync(observer, fixture, target, timeout.Token);
        Assert.Equal(state, NativeInspection.WindowState(first.Title));
        var clamped = new PixelRect(target.X, target.Y, first.MinimumWidth, first.MinimumHeight);
        Assert.Empty((await observer.ApplyAsync(new LayoutPlan([new(first.Id, target with { Width = 1, Height = 1 })]), timeout.Token)).Failures);
        await WaitForBoundsAsync(observer, fixture, clamped, timeout.Token);
        await fixture.CommandAsync("move");
        Assert.NotEqual(clamped, Find(await observer.CaptureAsync(timeout.Token), fixture).Bounds);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task FocusReportsVerifiedSuccessOrStructuredDenial()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var first = Find(await observer.CaptureAsync(timeout.Token), fixture);
        IWindowPlatform platform = observer;
        var result = await platform.ApplyAsync(new LayoutPlan([], first.Id), timeout.Token);
        if (result.Failures.Count == 0)
            Assert.Equal(first.Id, (await observer.CaptureAsync(timeout.Token)).FocusedWindowId);
        else
        {
            var failure = Assert.Single(result.Failures);
            Assert.Equal(first.Id, failure.WindowId);
            Assert.Contains(failure.Code, new[] { "focus_denied", "access_denied" });
            Assert.NotEmpty(failure.Message);
        }
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task RejectsDestroyedAndUnknownIdsAndInvalidBoundsWithoutMovingReplacement()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var first = Find(await observer.CaptureAsync(timeout.Token), fixture);
        await fixture.CommandAsync("destroy");
        await fixture.CommandAsync("create");
        // Deliberately apply before capturing the replacement: native identity must be checked at action time.
        var unknown = Guid.NewGuid();
        var result = await observer.ApplyAsync(new LayoutPlan([
            new(first.Id, new PixelRect(700, 600, 600, 400)), new(unknown, first.Bounds)], first.Id), timeout.Token);
        Assert.Equal(3, result.Failures.Count);
        Assert.All(result.Failures, failure => Assert.Equal("stale_window", failure.Code));
        var replacement = Find(await observer.CaptureAsync(timeout.Token), fixture);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(first.Bounds, replacement.Bounds);
        var invalid = await observer.ApplyAsync(new LayoutPlan([new(replacement.Id, replacement.Bounds with { Width = 0 })]), timeout.Token);
        Assert.Equal("invalid_bounds", Assert.Single(invalid.Failures).Code);
        Assert.Equal(replacement.Bounds, Find(await observer.CaptureAsync(timeout.Token), fixture).Bounds);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task SlowMinimumQueryIsBoundedAndPreCanceledApplyDoesNotMoveWindow()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var first = Find(await observer.CaptureAsync(timeout.Token), fixture);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.ApplyAsync(
            new LayoutPlan([new(first.Id, new PixelRect(800, 700, 600, 400))]), canceled.Token));
        Assert.Equal(first.Bounds, Find(await observer.CaptureAsync(timeout.Token), fixture).Bounds);
        await fixture.CommandAsync("slow-minimum");
        var watch = Stopwatch.StartNew();
        var snapshot = await observer.CaptureAsync(timeout.Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        var window = Find(snapshot, fixture);
        Assert.True(window.MinimumWidth > 0 && window.MinimumHeight > 0);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ConcurrentApplyRequestsCompleteOrFailCleanlyDuringDisposal()
    {
        var count = NativeInspection.ObserverWindowCount();
        await using var observer = new WindowsDesktopObserver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var pending = Enumerable.Range(0, 100)
            .Select(_ => observer.ApplyAsync(new LayoutPlan([]), timeout.Token)).ToArray();
        await observer.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        foreach (var apply in pending)
        {
            try { Assert.Empty((await apply.WaitAsync(timeout.Token)).Failures); }
            catch (ObjectDisposedException) { }
            catch (System.Threading.Channels.ChannelClosedException) { }
        }
        Assert.Equal(count, NativeInspection.ObserverWindowCount());
    }

    private static WindowInfo Find(DesktopSnapshot snapshot, NativeWindowFixture fixture) =>
        Assert.Single(snapshot.Windows, window => window.Title == fixture.Prefix + ".normal");

    private static async Task WaitForBoundsAsync(WindowsDesktopObserver observer, NativeWindowFixture fixture,
        PixelRect expected, CancellationToken cancellationToken)
    {
        while (Find(await observer.CaptureAsync(cancellationToken), fixture).Bounds != expected)
            await Task.Delay(30, cancellationToken);
    }
}