using System.Diagnostics;
using System.Runtime.Versioning;
using WinManager.Contracts;
using Windows.Win32.Foundation;
using Xunit;
using PInvoke = global::Windows.Win32.TestPInvoke;

namespace WinManager.Windows.Tests;

[Collection("Isolated interactive desktop")]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowsShortcutInteractiveTests
{
    private static readonly ShortcutBinding First = new(0x4007, 0x86, new ManagerCommand(CommandKind.Redraw));
    private static readonly ShortcutBinding Second = new(0x4007, 0x87, new ManagerCommand(CommandKind.Pause, Paused: true));

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ConflictRollsBackPartialRegistrationAndDisposeReleasesHotkeys()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var owner = new WindowsDesktopObserver();
        await using var contender = new WindowsDesktopObserver();
        await owner.StartAsync(timeout.Token);
        await contender.StartAsync(timeout.Token);
        Assert.Empty(await owner.RegisterShortcutsAsync([Second], timeout.Token));
        var failure = Assert.Single(await contender.RegisterShortcutsAsync([First, Second], timeout.Token));
        Assert.Equal("shortcut_conflict", failure.Code);
        Assert.Null(failure.WindowId);
        Assert.NotEmpty(failure.Message);
        // First was registered before the conflict; successful ownership here proves rollback.
        Assert.Empty(await owner.RegisterShortcutsAsync([First, Second], timeout.Token));
        for (var repeat = 0; repeat < 4; repeat++)
            Assert.Empty(await owner.RegisterShortcutsAsync([First, Second], timeout.Token));
        await owner.DisposeAsync();
        Assert.Empty(await contender.RegisterShortcutsAsync([First, Second], timeout.Token));
        Assert.Empty(await contender.RegisterShortcutsAsync([], timeout.Token));
        await using var replacement = new WindowsDesktopObserver();
        await replacement.StartAsync(timeout.Token);
        Assert.Empty(await replacement.RegisterShortcutsAsync([First, Second], timeout.Token));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task CallbackDeliversCorrectCommandsAndRejectsUnknownMismatchedAndOldIds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new WindowsDesktopObserver();
        await observer.StartAsync(timeout.Token);
        Assert.Empty(await observer.RegisterShortcutsAsync([First, Second], timeout.Token));
        await using var commands = observer.ShortcutCommands(timeout.Token).GetAsyncEnumerator();
        Post(observer, 999, First);
        Post(observer, 1, Second);
        Post(observer, 2, Second);
        Assert.True(await commands.MoveNextAsync());
        Assert.Equal(Second.Command, commands.Current);
        Post(observer, 1, First);
        Assert.True(await commands.MoveNextAsync());
        Assert.Equal(First.Command, commands.Current);
        Assert.Empty(await observer.RegisterShortcutsAsync([Second, First], timeout.Token));
        Post(observer, 1, First);
        Post(observer, 4, First);
        Assert.True(await commands.MoveNextAsync());
        Assert.Equal(First.Command, commands.Current);
        Post(observer, 3, Second);
        Assert.True(await commands.MoveNextAsync());
        Assert.Equal(Second.Command, commands.Current);
        await observer.DisposeAsync();
        Assert.False(await commands.MoveNextAsync());
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task InvalidBindingsReturnStructuredFailuresWithoutReplacingActiveSet()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new WindowsDesktopObserver();
        await observer.StartAsync(timeout.Token);
        Assert.Empty(await observer.RegisterShortcutsAsync([First], timeout.Token));
        foreach (var invalid in new[]
        {
            First with { Modifiers = 0x10 }, First with { VirtualKey = 0 },
            First with { VirtualKey = 0xFF }, First with { VirtualKey = 0x7B },
            First with { Command = null! }, null!
        })
            Assert.Equal("invalid_shortcut", Assert.Single(await observer.RegisterShortcutsAsync([invalid], timeout.Token)).Code);
        Assert.Equal("invalid_shortcut", Assert.Single(await observer.RegisterShortcutsAsync(
            Enumerable.Repeat(First, 257).ToArray(), timeout.Token)).Code);
        Assert.Equal("duplicate_shortcut", Assert.Single(await observer.RegisterShortcutsAsync(
            [First, First with { Modifiers = First.Modifiers & 0xF }], timeout.Token)).Code);
        await using var commands = observer.ShortcutCommands(timeout.Token).GetAsyncEnumerator();
        Post(observer, 1, First);
        Assert.True(await commands.MoveNextAsync());
        Assert.Equal(First.Command, commands.Current);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task FullCommandQueueDoesNotBlockNativeDispatchAndOldQueuedCommandsAreDropped()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new WindowsDesktopObserver();
        await observer.StartAsync(timeout.Token);
        Assert.Empty(await observer.RegisterShortcutsAsync([First], timeout.Token));
        for (var index = 0; index < 512; index++) Post(observer, 1, First);
        // Registration is ordered behind the posted messages; it also proves callback progress without a reader.
        Assert.Empty(await observer.RegisterShortcutsAsync([Second], timeout.Token));
        await using var commands = observer.ShortcutCommands(timeout.Token).GetAsyncEnumerator();
        var next = commands.MoveNextAsync().AsTask();
        Post(observer, 2, Second);
        Assert.True(await next);
        Assert.Equal(Second.Command, commands.Current);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task CancellationAndQueuedRegistrationDisposalCompleteCleanly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new WindowsDesktopObserver();
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.RegisterShortcutsAsync([First], timeout.Token));
        await observer.StartAsync(timeout.Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.RegisterShortcutsAsync([First], canceled.Token));
        await using (var commands = observer.ShortcutCommands(canceled.Token).GetAsyncEnumerator())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.MoveNextAsync().AsTask());
        var pending = Enumerable.Range(0, 100).Select(_ => observer.RegisterShortcutsAsync([First], timeout.Token)).ToArray();
        await observer.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        foreach (var task in pending)
        {
            try { Assert.Empty(await task.WaitAsync(timeout.Token)); }
            catch (ObjectDisposedException) { }
            catch (System.Threading.Channels.ChannelClosedException) { }
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => observer.RegisterShortcutsAsync([First], timeout.Token));
        await using var replacement = new WindowsDesktopObserver();
        await replacement.StartAsync(timeout.Token);
        Assert.Empty(await replacement.RegisterShortcutsAsync([First], timeout.Token));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task RestoreAndDisposeWaitForRecoveryHelperExit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new WindowsDesktopObserver();
        await observer.StartAsync(timeout.Token);
        using var helper = Process.GetProcessById(Assert.IsType<int>(observer.RecoveryHelperProcessId));
        Assert.False(helper.HasExited);
        Assert.Empty((await observer.RestoreAsync(timeout.Token)).Failures);
        Assert.True(helper.HasExited);
        Assert.Null(observer.RecoveryHelperProcessId);
        await using var other = new WindowsDesktopObserver();
        await other.StartAsync(timeout.Token);
        using var otherHelper = Process.GetProcessById(Assert.IsType<int>(other.RecoveryHelperProcessId));
        await other.DisposeAsync();
        Assert.True(otherHelper.HasExited);
    }

    private static void Post(WindowsDesktopObserver observer, int id, ShortcutBinding binding)
    {
        var packed = (binding.VirtualKey << 16) | (binding.Modifiers & 0xF);
        Assert.True(PInvoke.PostMessage(new HWND(observer.ShortcutNotificationHandle), 0x0312,
            new WPARAM((nuint)id), new LPARAM((nint)packed)));
    }
}