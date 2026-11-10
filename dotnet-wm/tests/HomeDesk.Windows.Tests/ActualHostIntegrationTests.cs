using System.Runtime.Versioning;
using System.Text.Json;
using WinManager.Contracts;
using WinManager.Core;
using Xunit;

namespace WinManager.Windows.Tests;

[Collection("Isolated interactive desktop")]
[SupportedOSPlatform("windows10.0.15063")]
public sealed class ActualHostIntegrationTests
{
    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ProductionHostSnapshotSubscribePauseWorkspaceEventsAndExit()
    {
        await using var window = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var host = await ActualHostFixture.CreateAsync(window);
        await host.StartAsync();
        var snapshot = await host.WaitForSnapshotAsync(state => state.Desktop.Windows.Count == 1);
        Assert.NotEqual(Guid.Empty, snapshot.InstanceId);
        Assert.Equal(new[] { "1", "2", "3" }, snapshot.Workspaces!
            .Where(workspace => workspace.MonitorId == snapshot.Desktop.Windows.Single().MonitorId).Select(workspace => workspace.Name));
        var subscription = await host.Client.SubscribeAsync(["pause.changed", "workspace.activated"], host.Token);
        await using var events = subscription.ReadEventsAsync(host.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var initial = events.Current;
        Assert.Equal("snapshot", initial.EventType);
        Assert.Equal(snapshot.InstanceId, initial.InstanceId);
        Assert.True((await host.Client.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token)).Success);
        Assert.True(await events.MoveNextAsync());
        var paused = events.Current;
        Assert.Equal("pause.changed", paused.EventType);
        Assert.True(paused.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
        Assert.True(paused.Sequence > initial.Sequence);
        Assert.True(paused.StateRevision > initial.StateRevision);
        Assert.True((await host.Client.ExecuteAsync(new(CommandKind.Pause, Paused: false), host.Token)).Success);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("pause.changed", events.Current.EventType);
        Assert.False(events.Current.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
        var previousSequence = events.Current.Sequence;
        Assert.True((await host.Client.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "2"), host.Token)).Success);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("workspace.activated", events.Current.EventType);
        Assert.True(events.Current.Sequence > previousSequence);
        Assert.Contains(events.Current.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Workspaces!,
            workspace => workspace.Name == "2" && workspace.Active);
        await subscription.UnsubscribeAsync(host.Token);
        await host.ExitAsync();
        Assert.True(NativeInspection.Visible(window.Prefix + ".normal"));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ProductionHostTilesTwoNativeWindowsWithConfiguredGaps()
    {
        await using var first = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var second = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var host = await ActualHostFixture.CreateAsync(first, second);
        await host.StartAsync();
        var state = await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Count == 2);
        var monitor = Assert.Single(state.Desktop.Monitors, monitor => monitor.Id == state.Desktop.Windows[0].MonitorId);
        Assert.All(state.Desktop.Windows, window => Assert.Equal(monitor.Id, window.MonitorId));
        var tree = new SplitTree();
        tree.Reconcile(state.Desktop.Windows);
        var plan = tree.Layout(monitor, 8, 8);
        Assert.Equal(2, plan.Placements.Count);
        var expected = plan.Placements.Select(placement => placement.Bounds).OrderBy(bounds => bounds.X).ThenBy(bounds => bounds.Y).ToArray();
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: host.HelperPath + ".inspection-missing");
        await observer.StartAsync(host.Token);
        while (true)
        {
            var actual = (await observer.CaptureAsync(host.Token)).Windows
                .Where(window => window.Title == first.Prefix + ".normal" || window.Title == second.Prefix + ".normal")
                .Select(window => window.Bounds).OrderBy(bounds => bounds.X).ThenBy(bounds => bounds.Y).ToArray();
            if (actual.SequenceEqual(expected)) break;
            await Task.Delay(40, host.Token);
        }
        Assert.Equal(2, expected.Length);
        Assert.True(expected[0].X + expected[0].Width < expected[1].X);
        await host.ExitAsync();
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ProductionHostWorkspaceHideAndNormalExitRestoreWithoutTouchingUnrelatedWindow()
    {
        await using var managed = await NativeWindowFixture.StartAsync(layoutOnly: true);
        var unrelatedTitle = "WinManager.ActualHost.Unrelated." + Guid.NewGuid().ToString("N");
        await using var unrelated = await OwnProcessWindow.CreateAsync(unrelatedTitle);
        var unrelatedHandle = NativeInspection.FindHandle(unrelatedTitle);
        var unrelatedShow = NativeInspection.ShowCommand(unrelatedTitle);
        var unrelatedBounds = ReadNativeBounds(unrelatedTitle);
        await using var host = await ActualHostFixture.CreateAsync(managed);
        await host.StartAsync();
        await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Count == 1);
        Assert.True((await host.Client.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "2"), host.Token)).Success);
        await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Single().ManagerHidden);
        Assert.False(NativeInspection.Visible(managed.Prefix + ".normal"));
        Assert.False(NativeInspection.Visible(managed.Prefix + ".hidden"));
        Assert.True(NativeInspection.Visible(unrelatedTitle));
        Assert.Equal(unrelatedShow, NativeInspection.ShowCommand(unrelatedTitle));
        Assert.Equal(unrelatedBounds, ReadNativeBounds(unrelatedTitle));
        await host.ExitAsync();
        Assert.True(NativeInspection.Visible(managed.Prefix + ".normal"));
        Assert.False(NativeInspection.Visible(managed.Prefix + ".hidden"));
        Assert.True(NativeInspection.Visible(unrelatedTitle));
        Assert.Equal(unrelatedHandle, NativeInspection.FindHandle(unrelatedTitle));
        Assert.Equal(unrelatedShow, NativeInspection.ShowCommand(unrelatedTitle));
        Assert.Equal(unrelatedBounds, ReadNativeBounds(unrelatedTitle));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ProductionHostCrashRestoresHiddenWindowButNotUnrelatedOrMismatchedIdentity()
    {
        await using var managed = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var unrelated = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var applicationHidden = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var mismatch = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await applicationHidden.CommandAsync("hide");
        await mismatch.CommandAsync("hide");
        var unrelatedBounds = ReadNativeBounds(unrelated.Prefix + ".normal");
        var mismatchBounds = ReadNativeBounds(mismatch.Prefix + ".normal");
        await using var host = await ActualHostFixture.CreateAsync(managed);
        host.AddMismatchedJournal(mismatch);
        await host.StartAsync();
        await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Count == 1);
        Assert.False(NativeInspection.Visible(mismatch.Prefix + ".normal"));
        Assert.True((await host.Client.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "2"), host.Token)).Success);
        await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Single().ManagerHidden);
        Assert.False(NativeInspection.Visible(managed.Prefix + ".normal"));
        await host.CrashAsync();
        Assert.True(NativeInspection.Visible(managed.Prefix + ".normal"));
        Assert.False(NativeInspection.Visible(managed.Prefix + ".hidden"));
        Assert.True(NativeInspection.Visible(unrelated.Prefix + ".normal"));
        Assert.Equal(unrelatedBounds, ReadNativeBounds(unrelated.Prefix + ".normal"));
        Assert.False(NativeInspection.Visible(applicationHidden.Prefix + ".normal"));
        Assert.False(NativeInspection.Visible(mismatch.Prefix + ".normal"));
        Assert.Equal(mismatchBounds, ReadNativeBounds(mismatch.Prefix + ".normal"));
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task ProductionHostMissingRecoveryExecutableRefusesWorkspaceHide()
    {
        await using var window = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var host = await ActualHostFixture.CreateAsync(window);
        await host.StartAsync(missingHelper: true);
        await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Count == 1);
        var result = await Assert.ThrowsAsync<WinManager.Client.WinManagerRemoteException>(() =>
            host.Client.ExecuteAsync(new(CommandKind.ActivateWorkspace, WorkspaceName: "2"), host.Token));
        Assert.Equal("recovery_unavailable", result.ErrorCode);
        Assert.True(NativeInspection.Visible(window.Prefix + ".normal"));
        Assert.False((await host.Client.QuerySnapshotAsync(host.Token)).Desktop.Windows.Single().ManagerHidden);
        Assert.True((await host.Client.ExecuteAsync(new(CommandKind.Exit), host.Token)).Success);
        await host.HostProcess.WaitForExitAsync(host.Token);
        Assert.Equal(0, host.HostProcess.ExitCode);
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task SecondProductionHostCustomPipeStillExitsFiveWithoutControls()
    {
        await using var window = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var host = await ActualHostFixture.CreateAsync(window);
        await host.StartAsync();
        var before = await host.WaitForSnapshotAsync(snapshot => snapshot.Desktop.Windows.Count == 1);
        var secondPipe = host.PipeName + ".second";
        var second = host.Launch(secondPipe);
        await second.WaitForExitAsync(host.Token);
        Assert.Equal(5, second.ExitCode);
        await Assert.ThrowsAsync<TimeoutException>(() => WinManager.Client.WinManagerClient.ConnectAsync(secondPipe, TimeSpan.FromMilliseconds(250), host.Token));
        var after = await host.Client.QuerySnapshotAsync(host.Token);
        Assert.Equal(before.InstanceId, after.InstanceId);
        Assert.False(after.Paused);
        Assert.Equal(before.Desktop.Windows.Single().Bounds, after.Desktop.Windows.Single().Bounds);
        Assert.True(NativeInspection.Visible(window.Prefix + ".normal"));
        await host.ExitAsync();
    }

    [IsolatedInteractiveFact]
    [Trait("Category", "IsolatedInteractive")]
    public async Task InvalidProductionConfigurationExitsTwoAndLogsFailureBeforeManagingWindows()
    {
        await using var window = await NativeWindowFixture.StartAsync(layoutOnly: true);
        await using var host = await ActualHostFixture.CreateAsync(window);
        File.WriteAllText(host.ConfigPath, "{\"innerGap\":-1}");
        var process = host.Launch();
        await process.WaitForExitAsync(host.Token);
        Assert.Equal(2, process.ExitCode);
        var diagnostic = await host.DiagnosticsAsync();
        Assert.Contains("Host startup or lifetime failed", diagnostic);
        Assert.Contains("Gaps must be between", diagnostic);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(host.DirectoryPath, "Data", "Logs")));
        Assert.True(NativeInspection.Visible(window.Prefix + ".normal"));
        await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: host.HelperPath + ".inspection-missing");
        await observer.StartAsync(host.Token);
        var actual = Assert.Single((await observer.CaptureAsync(host.Token)).Windows, entry => entry.Title == window.Prefix + ".normal");
        Assert.Equal(host.Before.Windows.Single(entry => entry.Title == actual.Title).Bounds, actual.Bounds);
    }

    private static PixelRect ReadNativeBounds(string title)
    {
        var window = new global::Windows.Win32.Foundation.HWND(NativeInspection.FindHandle(title));
        Assert.True(global::Windows.Win32.TestPInvoke.GetWindowRect(window, out var rectangle));
        return new(rectangle.left, rectangle.top, rectangle.right - rectangle.left, rectangle.bottom - rectangle.top);
    }
}