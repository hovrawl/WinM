using System.Runtime.Versioning;
using WinManager.Contracts;
using Xunit;

namespace WinManager.Windows.Tests;

[CollectionDefinition("Isolated interactive desktop", DisableParallelization = true)]
public sealed class IsolatedInteractiveCollection;

[Collection("Isolated interactive desktop")]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowsDesktopObserverInteractiveTests
{
    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task DiscoversPreexistingWindowsAndFiltersToolOwnedHiddenAndExplicitIgnores()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var ownWindow = await OwnProcessWindow.CreateAsync(fixture.Prefix + ".own-process");
        var count = NativeInspection.ObserverWindowCount();
        await using (var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await observer.StartAsync(timeout.Token);
            Assert.Equal(count + 1, NativeInspection.ObserverWindowCount());
            var first = await observer.CaptureAsync(timeout.Token);
            var candidates = first.Windows.Where(window => window.Title.StartsWith(fixture.Prefix, StringComparison.Ordinal)).ToArray();
            Assert.Equal(5, candidates.Length);
            foreach (var name in new[] { "normal", "popup", "tool", "owned", "noactivate" })
            {
                var candidate = Assert.Single(candidates, candidate => candidate.Title == fixture.Prefix + "." + name);
                Assert.Equal(name == "popup", candidate.IsPopup);
                Assert.Equal(name == "tool", candidate.IsToolWindow);
                Assert.Equal(name == "owned", candidate.IsOwned);
                Assert.Equal(name == "noactivate", candidate.IsNoActivate);
                Assert.Equal(name != "normal", candidate.IsIgnored);
                Assert.Null(candidate.TilingOverride);
                Assert.Equal(fixture.WindowClass, candidate.ClassName);
            }
            foreach (var name in new[] { "hidden", "child", "ignored", "own-process" })
                Assert.DoesNotContain(first.Windows, candidate => candidate.Title == fixture.Prefix + "." + name);
            var window = Assert.Single(candidates, window => window.Title == fixture.Prefix + ".normal");
            Assert.Equal(fixture.Prefix + ".normal", window.Title);
            Assert.Equal(fixture.WindowClass, window.ClassName);
            Assert.NotEqual(Guid.Empty, window.Id);
            Assert.True(window.Visible);
            Assert.False(window.Minimized);
            Assert.NotEmpty(window.ProcessName);
            Assert.NotEmpty(first.Monitors);
            Assert.All(first.Monitors, monitor =>
            {
                Assert.NotEqual(Guid.Empty, monitor.Id);
                Assert.True(monitor.Dpi > 0);
                Assert.True(monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0);
                Assert.True(monitor.WorkArea.Width > 0 && monitor.WorkArea.Height > 0);
            });
            Assert.Contains(first.Monitors, monitor => monitor.Id == window.MonitorId);
            Assert.Equal(NativeInspection.WindowDpi(window.Title), Assert.Single(first.Monitors, monitor => monitor.Id == window.MonitorId).Dpi);
            Assert.DoesNotContain(first.Windows, candidate => candidate.ClassName.StartsWith("WinManager.Observer.", StringComparison.Ordinal));
            var second = await observer.CaptureAsync(timeout.Token);
            Assert.All(candidates, candidate =>
                Assert.Equal(candidate.Id, Assert.Single(second.Windows, entry => entry.Title == candidate.Title).Id));
            Assert.Equal(first.Monitors.ToArray(), second.Monitors.ToArray());

            await using var ignoreProcess = new WindowsDesktopObserver(ignoredProcessNames: [window.ProcessName.ToUpperInvariant() + ".exe"]);
            await ignoreProcess.StartAsync(timeout.Token);
            Assert.DoesNotContain((await ignoreProcess.CaptureAsync(timeout.Token)).Windows,
                candidate => candidate.Title.StartsWith(fixture.Prefix, StringComparison.Ordinal));
        }
        Assert.Equal(count, NativeInspection.ObserverWindowCount());
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ReconcilesRenameMoveMinimizeVisibilityCloakAndDestroyWithoutReusingIdentity()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await observer.StartAsync(timeout.Token);
        var first = Find(await observer.CaptureAsync(timeout.Token), fixture.Prefix + ".normal");
        Assert.NotNull(first);
        await fixture.CommandAsync("rename");
        var renamed = await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed") is not null, timeout.Token);
        Assert.Equal(first.Id, Find(renamed, fixture.Prefix + ".renamed")!.Id);
        await fixture.CommandAsync("move");
        var moved = await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed")?.Bounds != first.Bounds, timeout.Token);
        Assert.Equal(first.Id, Find(moved, fixture.Prefix + ".renamed")!.Id);
        await fixture.CommandAsync("fullscreen");
        await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed")?.Fullscreen == true, timeout.Token);
        await fixture.CommandAsync("minimize");
        await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed")?.Minimized == true, timeout.Token);
        await fixture.CommandAsync("restore");
        await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed")?.Minimized == false, timeout.Token);
        await fixture.CommandAsync("hide");
        await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed") is null, timeout.Token);
        await fixture.CommandAsync("show");
        var shown = await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed") is not null, timeout.Token);
        Assert.Equal(first.Id, Find(shown, fixture.Prefix + ".renamed")!.Id);
        await fixture.CommandAsync("cloak");
        await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed") is null, timeout.Token);
        await fixture.CommandAsync("uncloak");
        var uncloaked = await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed") is not null, timeout.Token);
        Assert.Equal(first.Id, Find(uncloaked, fixture.Prefix + ".renamed")!.Id);
        await fixture.CommandAsync("destroy");
        await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".renamed") is null, timeout.Token);
        await fixture.CommandAsync("create");
        var created = await EventuallyAsync(observer, snapshot => Find(snapshot, fixture.Prefix + ".normal") is not null, timeout.Token);
        Assert.NotEqual(first.Id, Find(created, fixture.Prefix + ".normal")!.Id);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task EmitsWindowFocusAndDisplayNotificationsAndSupportsConcurrentCaptures()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(ignoredClassNames: [fixture.IgnoredClass]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await observer.StartAsync(timeout.Token);
        await using var notifications = observer.Notifications(timeout.Token).GetAsyncEnumerator();
        Assert.True(await notifications.MoveNextAsync());
        await fixture.CommandAsync("rename");
        Assert.True(await notifications.MoveNextAsync());
        await fixture.CommandAsync("focus");
        Assert.True(await notifications.MoveNextAsync());
        var focused = await EventuallyAsync(observer, snapshot =>
            Find(snapshot, fixture.Prefix + ".renamed")?.Id == snapshot.FocusedWindowId, timeout.Token);
        Assert.Equal(Find(focused, fixture.Prefix + ".renamed")!.Id, focused.FocusedWindowId);
        await fixture.CommandAsync("display");
        Assert.True(await notifications.MoveNextAsync());
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => observer.CaptureAsync(timeout.Token)));
        Assert.All(snapshots, snapshot => Assert.Equal(focused.Monitors.ToArray(), snapshot.Monitors.ToArray()));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task CancellationAndRepeatedStartupDisposalLeaveNoNotificationWindows()
    {
        var count = NativeInspection.ObserverWindowCount();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var observer = new WindowsDesktopObserver();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await observer.StartAsync(timeout.Token);
            await observer.StartAsync(timeout.Token);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.CaptureAsync(canceled.Token));
            var pending = Enumerable.Range(0, 100).Select(_ => observer.CaptureAsync(timeout.Token)).ToArray();
            await observer.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            foreach (var capture in pending)
            {
                try { await capture.WaitAsync(timeout.Token); }
                catch (ObjectDisposedException) { }
                catch (System.Threading.Channels.ChannelClosedException) { }
            }
            Assert.Equal(count, NativeInspection.ObserverWindowCount());
        }
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var observer = new WindowsDesktopObserver();
            using var cancellation = new CancellationTokenSource();
            var starting = observer.StartAsync(cancellation.Token);
            cancellation.Cancel();
            try { await starting.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { }
            await observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(count, NativeInspection.ObserverWindowCount());
        }
    }

    private static WindowInfo? Find(DesktopSnapshot snapshot, string title) => snapshot.Windows.SingleOrDefault(window => window.Title == title);

    private static async Task<DesktopSnapshot> EventuallyAsync(WindowsDesktopObserver observer,
        Func<DesktopSnapshot, bool> condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await observer.CaptureAsync(cancellationToken);
            if (condition(snapshot)) return snapshot;
            await Task.Delay(30, cancellationToken);
        }
    }
}