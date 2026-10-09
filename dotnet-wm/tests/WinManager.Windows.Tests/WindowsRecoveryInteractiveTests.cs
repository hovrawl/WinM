using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Windows.Tests;

[Collection("Isolated interactive desktop")]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class WindowsRecoveryInteractiveTests
{
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "Recovery", "WinManager.Recovery.exe");

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task HideRetainsGuidAndActualVisibilityThenShowAndRestoreWorkRepeatedly()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await observer.StartAsync(timeout.Token);
        var original = Find(await observer.CaptureAsync(timeout.Token), fixture);
        for (var repeat = 0; repeat < 3; repeat++)
        {
            Assert.Empty((await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(original.Id, false)]), timeout.Token)).Failures);
            var hidden = Find(await observer.CaptureAsync(timeout.Token), fixture);
            Assert.Equal(original.Id, hidden.Id);
            Assert.False(hidden.Visible);
            Assert.True(hidden.ManagerHidden);
            Assert.False(NativeInspection.Visible(original.Title));
            var result = repeat == 0
                ? await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(original.Id, true)]), timeout.Token)
                : await observer.RestoreAsync(timeout.Token);
            Assert.Empty(result.Failures);
            var shown = Find(await observer.CaptureAsync(timeout.Token), fixture);
            Assert.Equal(original.Id, shown.Id);
            Assert.True(shown.Visible);
            Assert.False(shown.ManagerHidden);
        }
        Assert.Empty((await observer.RestoreAsync(timeout.Token)).Failures);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task MissingHelperRefusesHideAndAppHiddenOrMinimizedAreNotManagerHidden()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper + ".missing");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var original = Find(await observer.CaptureAsync(timeout.Token), fixture);
        var result = await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(original.Id, false)]), timeout.Token);
        Assert.Equal("recovery_unavailable", Assert.Single(result.Failures).Code);
        Assert.True(NativeInspection.Visible(original.Title));
        await fixture.CommandAsync("minimize");
        var minimized = Find(await observer.CaptureAsync(timeout.Token), fixture);
        Assert.True(minimized.Minimized);
        Assert.False(minimized.ManagerHidden);
        result = await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(minimized.Id, false)]), timeout.Token);
        Assert.Equal("ineligible_window", Assert.Single(result.Failures).Code);
        await fixture.CommandAsync("hide");
        Assert.DoesNotContain((await observer.CaptureAsync(timeout.Token)).Windows, window => window.Title == original.Title);
        Assert.Equal("stale_window", Assert.Single((await observer.ApplyAsync(
            new LayoutPlan([], Visibility: [new(original.Id, true)]), timeout.Token)).Failures).Code);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task DisposeRestoresManagerHiddenWindowButDoesNotShowApplicationHiddenWindow()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper);
        await observer.StartAsync(CancellationToken.None);
        var original = Find(await observer.CaptureAsync(CancellationToken.None), fixture);
        Assert.Empty((await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(original.Id, false)]), CancellationToken.None)).Failures);
        await observer.DisposeAsync();
        Assert.True(NativeInspection.Visible(original.Title));
        Assert.False(NativeInspection.Visible(fixture.Prefix + ".hidden"));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ForcedHostExitRestoresOnlyRegisteredWindowsWithIndependentHelper()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var unrelated = await NativeWindowFixture.StartAsync();
        await unrelated.CommandAsync("hide");
        using var host = StartHost(fixture.Prefix + ".normal");
        try
        {
            Assert.Equal("HIDDEN", await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var helperPid = int.Parse((await host.StandardOutput.ReadLineAsync())!, System.Globalization.CultureInfo.InvariantCulture);
            using var helper = Process.GetProcessById(helperPid);
            using var helperHandle = global::Windows.Win32.TestPInvoke.OpenProcess_SafeHandle(
                global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)helperPid);
            Assert.False(helperHandle.IsInvalid);
            Assert.False(NativeInspection.Visible(fixture.Prefix + ".normal"));
            host.Kill();
            await host.WaitForExitAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!NativeInspection.Visible(fixture.Prefix + ".normal")) await Task.Delay(30, timeout.Token);
            Assert.False(NativeInspection.Visible(unrelated.Prefix + ".normal"));
            Assert.False(NativeInspection.Visible(fixture.Prefix + ".hidden"));
            await helper.WaitForExitAsync(timeout.Token);
            Assert.True(global::Windows.Win32.TestPInvoke.GetExitCodeProcess(helperHandle, out var exitCode));
            Assert.Equal(0u, exitCode);
        }
        finally
        {
            if (!host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
        }
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task IdentityMismatchNeverShowsWindowAndStaleJournalSkipsLiveHost()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        var handle = NativeInspection.FindHandle(fixture.Prefix + ".normal");
        var record = new RecoveryRecord(handle, 0, 0, 4, "WinManager.Recovery." + Guid.NewGuid().ToString("N"));
        Assert.True(RecoveryNative.Mark(record));
        await fixture.CommandAsync("hide");
        Assert.True(RecoveryNative.Restore(record));
        Assert.False(NativeInspection.Visible(fixture.Prefix + ".normal"));
        // The journal scanner must also leave records belonging to a live identity-matched host alone.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinManager", "Recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "WinManager.Recovery." + Guid.NewGuid().ToString("N") + ".json");
        Assert.True(RecoveryNative.TryProcessTime((uint)Environment.ProcessId, out var time));
        var targetPid = NativeInspection.ProcessId(fixture.Prefix + ".normal");
        Assert.True(RecoveryNative.TryProcessTime(targetPid, out var targetTime));
        var liveRecord = record with { ProcessId = targetPid, CreationTime = targetTime, HideConfirmed = true };
        Assert.True(RecoveryNative.Matches(liveRecord));
        Assert.True(RecoveryNative.Restore(liveRecord with { CreationTime = targetTime + 1 }));
        Assert.True(RecoveryNative.Restore(liveRecord with { Property = liveRecord.Property + ".wrong" }));
        Assert.False(NativeInspection.Visible(fixture.Prefix + ".normal"));
        var journal = new RecoveryJournal((uint)Environment.ProcessId, time, [liveRecord]);
        File.WriteAllText(path, JsonSerializer.Serialize(journal));
        try
        {
            await using var other = await NativeWindowFixture.StartAsync();
            await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper);
            await observer.StartAsync(CancellationToken.None);
            var window = Find(await observer.CaptureAsync(CancellationToken.None), other);
            Assert.Empty((await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(window.Id, false)]), CancellationToken.None)).Failures);
            Assert.True(File.Exists(path));
            Assert.False(NativeInspection.Visible(fixture.Prefix + ".normal"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task HelperDeathRestoresHiddenWindowAndPermanentlyRefusesFurtherHides()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await observer.StartAsync(timeout.Token);
        var window = Find(await observer.CaptureAsync(timeout.Token), fixture);
        Assert.Empty((await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(window.Id, false)]), timeout.Token)).Failures);
        using var helper = Process.GetProcessById(observer.RecoveryHelperProcessId!.Value);
        helper.Kill();
        await helper.WaitForExitAsync(timeout.Token);
        // Do not capture or apply: restoration must be driven by the independent native-thread watchdog.
        while (!NativeInspection.Visible(window.Title)) await Task.Delay(30, timeout.Token);
        var restored = Find(await observer.CaptureAsync(timeout.Token), fixture);
        Assert.False(restored.ManagerHidden);
        Assert.Equal(window.Id, restored.Id);
        Assert.Equal("recovery_unavailable", Assert.Single((await observer.ApplyAsync(
            new LayoutPlan([], Visibility: [new(window.Id, false)]), timeout.Token)).Failures).Code);
        Assert.True(NativeInspection.Visible(window.Title));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task RegistrationWithoutAcknowledgmentNeverHidesWindow()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Path.Combine(AppContext.BaseDirectory, "WinManager.Windows.Tests.exe"));
        var previous = Environment.GetEnvironmentVariable("WINMANAGER_TEST_RECOVERY_NO_ACK");
        Environment.SetEnvironmentVariable("WINMANAGER_TEST_RECOVERY_NO_ACK", "1");
        try
        {
            await observer.StartAsync(CancellationToken.None);
            var window = Find(await observer.CaptureAsync(CancellationToken.None), fixture);
            var failures = (await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(window.Id, false)]), CancellationToken.None)).Failures;
            Assert.Equal("recovery_unavailable", Assert.Single(failures).Code);
            Assert.True(NativeInspection.Visible(window.Title));
            Assert.False(Find(await observer.CaptureAsync(CancellationToken.None), fixture).ManagerHidden);
        }
        finally { Environment.SetEnvironmentVariable("WINMANAGER_TEST_RECOVERY_NO_ACK", previous); }
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task NewHelperRestoresInterruptedLaunchJournalButRejectsReusedWindowMarker()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        var title = fixture.Prefix + ".normal";
        var pid = NativeInspection.ProcessId(title);
        Assert.True(RecoveryNative.TryProcessTime(pid, out var time));
        Assert.True(RecoveryNative.TryProcessTime((uint)Environment.ProcessId, out var hostTime));
        var record = new RecoveryRecord(NativeInspection.FindHandle(title), pid, time, 4,
            "WinManager.Recovery." + Guid.NewGuid().ToString("N"), true);
        Assert.True(RecoveryNative.Mark(record));
        await fixture.CommandAsync("hide");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinManager", "Recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "WinManager.Recovery." + Guid.NewGuid().ToString("N") + ".json");
        // Same PID but a different launch identity is dead ownership, not a live host.
        File.WriteAllText(path, JsonSerializer.Serialize(new RecoveryJournal((uint)Environment.ProcessId, hostTime + 1, [record])));
        try
        {
            await using var other = await NativeWindowFixture.StartAsync();
            await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper);
            await observer.StartAsync(CancellationToken.None);
            Assert.True(NativeInspection.Visible(title));
            Assert.False(File.Exists(path));
            var window = Find(await observer.CaptureAsync(CancellationToken.None), other);
            Assert.Empty((await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(window.Id, false)]), CancellationToken.None)).Failures);
            Assert.True(NativeInspection.Visible(title));
            Assert.False(File.Exists(path));
            await fixture.CommandAsync("hide");
            // Successful recovery removes the generation marker: a stale duplicate cannot show it again.
            Assert.True(RecoveryNative.Restore(record));
            Assert.False(NativeInspection.Visible(title));
        }
        finally { File.Delete(path); File.Delete(path + ".lock"); }
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task RestorePreservesOriginalMaximizedStateAndUnknownIdsAreRefused()
    {
        await using var fixture = await NativeWindowFixture.StartAsync();
        await fixture.CommandAsync("maximize");
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: Helper);
        await observer.StartAsync(CancellationToken.None);
        var window = Find(await observer.CaptureAsync(CancellationToken.None), fixture);
        var original = NativeInspection.ShowCommand(window.Title);
        Assert.Equal(global::Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED, original);
        Assert.Equal("stale_window", Assert.Single((await observer.ApplyAsync(
            new LayoutPlan([], Visibility: [new(Guid.NewGuid(), false)]), CancellationToken.None)).Failures).Code);
        Assert.Empty((await observer.ApplyAsync(new LayoutPlan([], Visibility: [new(window.Id, false)]), CancellationToken.None)).Failures);
        Assert.Empty((await observer.RestoreAsync(CancellationToken.None)).Failures);
        Assert.True(NativeInspection.Visible(window.Title));
        Assert.Equal(original, NativeInspection.ShowCommand(window.Title));
    }

    private static Process StartHost(string title)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(TestWindowProgram).Assembly.Location);
        info.ArgumentList.Add("--recovery-test-host");
        info.ArgumentList.Add(title);
        info.ArgumentList.Add(Helper);
        return Process.Start(info)!;
    }

    private static WindowInfo Find(DesktopSnapshot snapshot, NativeWindowFixture fixture) =>
        Assert.Single(snapshot.Windows, window => window.Title == fixture.Prefix + ".normal");
}