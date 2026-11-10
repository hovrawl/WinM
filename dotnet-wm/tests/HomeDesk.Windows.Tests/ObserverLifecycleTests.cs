using System.Runtime.Versioning;
using Xunit;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class ObserverLifecycleTests
{
    [Fact]
    public async Task ApplyBeforeStartAndAfterDisposalIsRejectedWithoutTouchingTheDesktop()
    {
        var observer = new WindowsDesktopObserver();
        var plan = new WinManager.Core.LayoutPlan([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.ApplyAsync(plan, CancellationToken.None));
        await observer.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => observer.ApplyAsync(plan, CancellationToken.None));
    }

    [Fact]
    public async Task CaptureBeforeStartIsRejectedWithoutTouchingTheDesktop()
    {
        await using var observer = new WindowsDesktopObserver();
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.CaptureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreCanceledStartDoesNotInitializeNativeResources()
    {
        await using var observer = new WindowsDesktopObserver();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.StartAsync(cancellation.Token));
    }

    [Fact]
    public async Task DisposalBeforeStartIsIdempotentAndCompletesNotifications()
    {
        var observer = new WindowsDesktopObserver();
        await observer.DisposeAsync();
        await observer.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => observer.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => observer.CaptureAsync(CancellationToken.None));
        await using var notifications = observer.Notifications(CancellationToken.None).GetAsyncEnumerator();
        Assert.False(await notifications.MoveNextAsync());
    }
}