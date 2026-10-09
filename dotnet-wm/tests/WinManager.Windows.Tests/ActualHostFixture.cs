using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;
using Xunit;

namespace WinManager.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
internal sealed class ActualHostFixture : IAsyncDisposable
{
    private readonly List<Process> children = [];
    private readonly List<Task<string>> output = [];
    private readonly List<string> journalPaths = [];
    private readonly HashSet<string> allowedTitles;
    private Process? helper;
    private IDisposable? helperHandle;
    private Func<uint>? helperExitCode;
    public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "ActualHostArtifacts", Guid.NewGuid().ToString("N"));
    public string PipeName { get; } = "WinManager.ActualHost.Tests." + Guid.NewGuid().ToString("N");
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public string HelperPath => Path.Combine(DirectoryPath, "Host", "WinManager.Recovery.exe");
    public Process HostProcess { get; private set; } = null!;
    public WinManagerClient Client { get; private set; } = null!;
    public DesktopSnapshot Before { get; private set; } = null!;
    public CancellationTokenSource Deadline { get; } = new(TimeSpan.FromSeconds(60));
    public CancellationToken Token => Deadline.Token;

    private ActualHostFixture(IEnumerable<NativeWindowFixture> windows)
    {
        allowedTitles = windows.Select(window => window.Prefix + ".normal").ToHashSet(StringComparer.Ordinal);
    }

    public static async Task<ActualHostFixture> CreateAsync(params NativeWindowFixture[] windows)
    {
        if (Environment.GetEnvironmentVariable("WINMANAGER_ISOLATED_INTERACTIVE") != "1")
            throw new InvalidOperationException("Actual Host tests require an isolated interactive desktop.");
        var fixture = new ActualHostFixture(windows);
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "ActualHost");
            var destination = Path.Combine(fixture.DirectoryPath, "Host");
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
            await using var observer = new WindowsDesktopObserver(recoveryExecutablePath: fixture.HelperPath + ".capture-missing");
            await observer.StartAsync(fixture.Token);
            fixture.Before = await observer.CaptureAsync(fixture.Token);
            Assert.Equal(windows.Length, fixture.Before.Windows.Count(window => fixture.allowedTitles.Contains(window.Title)));
            var other = fixture.Before.Windows.Where(window => !fixture.allowedTitles.Contains(window.Title)).ToArray();
            var retainedProcesses = fixture.Before.Windows.Where(window => fixture.allowedTitles.Contains(window.Title))
                .Select(window => window.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ignoredProcesses = other.Select(window => window.ProcessName).Where(name => !retainedProcesses.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ignoredClasses = other.Select(window => window.ClassName).Concat(windows.Select(window => window.IgnoredClass))
                .Append("STATIC").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.DoesNotContain(windows, window => ignoredClasses.Contains(window.WindowClass, StringComparer.OrdinalIgnoreCase));
            var targetMonitor = fixture.Before.Monitors.Single(monitor => monitor.Id ==
                fixture.Before.Windows.First(window => fixture.allowedTitles.Contains(window.Title)).MonitorId).DeviceName;
            File.WriteAllText(fixture.ConfigPath, JsonSerializer.Serialize(new
            {
                workspaces = new[] { new WorkspaceDefinition("1", targetMonitor), new WorkspaceDefinition("2", targetMonitor),
                    new WorkspaceDefinition("3", targetMonitor) },
                innerGap = 8, outerGap = 8,
                ignoredProcessNames = ignoredProcesses, ignoredClassNames = ignoredClasses, shortcuts = Array.Empty<ShortcutBinding>()
            }, Protocol.JsonOptions));
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public Process Launch(string? pipeName = null, string? recoveryExecutable = null)
    {
        var info = new ProcessStartInfo(Path.Combine(DirectoryPath, "Host", "WinManager.Host.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = DirectoryPath
        };
        foreach (var argument in new[] { "--config", ConfigPath, "--data-dir", Path.Combine(DirectoryPath, "Data"),
            "--pipe", pipeName ?? PipeName, "--recovery-executable", recoveryExecutable ?? HelperPath })
            info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        try { Assert.True(process.Start()); }
        catch { process.Dispose(); throw; }
        children.Add(process);
        output.Add(process.StandardOutput.ReadToEndAsync());
        output.Add(process.StandardError.ReadToEndAsync());
        return process;
    }

    public async Task StartAsync(bool missingHelper = false)
    {
        HostProcess = Launch(recoveryExecutable: missingHelper ? HelperPath + ".missing" : null);
        while (true)
        {
            if (HostProcess.HasExited)
                throw new InvalidOperationException($"Actual Host exited {HostProcess.ExitCode}: {await DiagnosticsAsync()}");
            try
            {
                Client ??= await WinManagerClient.ConnectAsync(PipeName, TimeSpan.FromMilliseconds(250), Token);
                var snapshot = await Client.QuerySnapshotAsync(Token);
                Assert.All(snapshot.Desktop.Windows, window => Assert.Contains(window.Title, allowedTitles));
                if (!missingHelper)
                {
                    helper = FindOwnHelper();
                    if (helper is not null)
                    {
                        var handle = global::Windows.Win32.TestPInvoke.OpenProcess_SafeHandle(
                            global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
                            false, (uint)helper.Id);
                        helperHandle = handle;
                        Assert.False(handle.IsInvalid);
                        helperExitCode = () =>
                        {
                            Assert.True(global::Windows.Win32.TestPInvoke.GetExitCodeProcess(handle, out var code));
                            return code;
                        };
                        TrackOwnJournal();
                    }
                }
                Assert.True(missingHelper || helper is not null, "Production recovery child was not running.");
                return;
            }
            catch (TimeoutException) { await Task.Delay(30, Token); }
            catch (WinManagerRemoteException error) when (error.ErrorCode == "not_ready")
            {
                await Task.Delay(30, Token);
            }
        }
    }

    private Process? FindOwnHelper()
    {
        foreach (var process in Process.GetProcessesByName("WinManager.Recovery"))
        {
            var retained = false;
            try
            {
                if (!process.HasExited && string.Equals(process.MainModule?.FileName, HelperPath, StringComparison.OrdinalIgnoreCase))
                {
                    retained = true;
                    return process;
                }
            }
            finally { if (!retained) process.Dispose(); }
        }
        return null;
    }

    private void TrackOwnJournal()
    {
        Assert.True(RecoveryNative.TryProcessTime((uint)HostProcess.Id, out var hostTime));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinManager", "Recovery");
        var owned = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "WinManager.Recovery.*.json"))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllBytes(path));
                if (journal is not null && journal.HostId == (uint)HostProcess.Id && journal.HostCreationTime == hostTime)
                    owned.Add(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        journalPaths.Add(Assert.Single(owned));
    }

    public async Task<ManagerSnapshot> WaitForSnapshotAsync(Func<ManagerSnapshot, bool> predicate)
    {
        ManagerSnapshot? last = null;
        try
        {
            while (true)
            {
                last = await Client.QuerySnapshotAsync(Token);
                Assert.All(last.Desktop.Windows, window => Assert.Contains(window.Title, allowedTitles));
                if (predicate(last)) return last;
                await Task.Delay(40, Token);
            }
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"Snapshot condition timed out. Last state: {JsonSerializer.Serialize(last, Protocol.JsonOptions)}", error);
        }
    }

    public async Task ExitAsync()
    {
        Assert.True((await Client.ExecuteAsync(new(CommandKind.Exit), Token)).Success);
        await HostProcess.WaitForExitAsync(Token);
        Assert.True(HostProcess.ExitCode == 0, await DiagnosticsAsync());
        await AssertHelperExitedAsync();
    }

    public async Task CrashAsync()
    {
        HostProcess.Kill();
        await HostProcess.WaitForExitAsync(Token);
        await AssertHelperExitedAsync();
    }

    private async Task AssertHelperExitedAsync()
    {
        Assert.NotNull(helper);
        await helper.WaitForExitAsync(Token);
        Assert.NotNull(helperExitCode);
        Assert.Equal(0u, helperExitCode());
    }

    public void AddMismatchedJournal(NativeWindowFixture fixture)
    {
        var title = fixture.Prefix + ".normal";
        var pid = NativeInspection.ProcessId(title);
        Assert.True(RecoveryNative.TryProcessTime(pid, out var time));
        Assert.True(RecoveryNative.TryProcessTime((uint)Environment.ProcessId, out var hostTime));
        var record = new RecoveryRecord(NativeInspection.FindHandle(title), pid, time + 1, 4,
            "WinManager.Recovery." + Guid.NewGuid().ToString("N"), true);
        Assert.True(RecoveryNative.Mark(record));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinManager", "Recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "WinManager.Recovery." + Guid.NewGuid().ToString("N") + ".json");
        journalPaths.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(new RecoveryJournal((uint)Environment.ProcessId, hostTime + 1, [record])));
    }

    public async Task<string> DiagnosticsAsync()
    {
        var text = string.Join(Environment.NewLine, await Task.WhenAll(output));
        var logs = Path.Combine(DirectoryPath, "Data", "Logs");
        if (Directory.Exists(logs))
            foreach (var path in Directory.EnumerateFiles(logs)) text += Environment.NewLine + await File.ReadAllTextAsync(path);
        return text;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client is not null && HostProcess is { HasExited: false })
            {
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await Client.ExecuteAsync(new(CommandKind.Exit), stop.Token);
                    await HostProcess.WaitForExitAsync(stop.Token);
                }
                catch (Exception) { }
            }
            foreach (var child in children)
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            helper ??= FindOwnHelper();
            if (helper is not null && !helper.HasExited)
            {
                try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
                catch (TimeoutException)
                {
                    helper.Kill();
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            if (Client is not null) await Client.DisposeAsync();
            await Task.WhenAll(output).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var child in children) child.Dispose();
            helper?.Dispose();
            helperHandle?.Dispose();
            Deadline.Dispose();
            foreach (var path in journalPaths)
            {
                File.Delete(path);
                File.Delete(path + ".lock");
                File.Delete(path + ".tmp");
            }
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}