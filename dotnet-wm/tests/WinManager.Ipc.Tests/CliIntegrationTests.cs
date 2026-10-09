using System.Diagnostics;
using System.Text.Json;
using WinManager.Client;
using WinManager.Contracts;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class CliIntegrationTests
{
    [Theory]
    [InlineData("0\n")]
    [InlineData("")]
    public async Task ExecutableWithoutArgumentsShowsMenuAndExitsCleanly(string input)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var cli = CliProcess.Start();
        await cli.SendAsync(input, timeout.Token);
        await ReadMenuAsync(cli, timeout.Token);
        Assert.Equal(0, await cli.ExitAsync(timeout.Token));
        Assert.Null(await cli.ReadLineAsync(timeout.Token));
        Assert.Equal("", await cli.Errors.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task InteractiveHelpAndInvalidSelectionReturnToMenu()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var cli = CliProcess.Start();
        await cli.SendAsync("invalid\n3\n0\n", timeout.Token);
        await ReadMenuAsync(cli, timeout.Token);
        Assert.Equal("Choose 1, 2, 3, or 0.", await cli.ReadLineAsync(timeout.Token));
        await ReadMenuAsync(cli, timeout.Token);
        var help = new List<string>();
        string? line;
        while ((line = await cli.ReadLineAsync(timeout.Token)) != "Select an option:")
        {
            Assert.NotNull(line);
            help.Add(line);
        }
        Assert.Contains("  status [--reconnect] [--pipe NAME]", help);
        Assert.Contains("  command JSON [--pipe NAME]", help);
        Assert.Contains("1. Status observer", help);
        Assert.Equal(0, await cli.ExitAsync(timeout.Token));
        Assert.Equal("", await cli.Errors.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task InteractiveCommandsRecoverFromInvalidJsonAndSendRepeatedCommands()
    {
        await using var host = await PipeHost.StartAsync();
        await using var cli = CliProcess.Start();
        await cli.SendAsync($"2\n{host.Name}\n{{\n{{\"kind\":\"pause\"}}\n{{\"kind\":\"pause\",\"paused\":true}}\n{{\"kind\":\"pause\",\"paused\":false}}\n\n0\n", host.Token);
        await ReadMenuAsync(cli, host.Token);
        Assert.Equal("Pipe name (blank for default):", await cli.ReadLineAsync(host.Token));
        for (var i = 0; i < 3; i++)
            Assert.Equal("Enter command JSON (blank to return to menu):", await cli.ReadLineAsync(host.Token));
        Assert.True((await cli.ReadAsync<CommandResult>(host.Token)).Success);
        Assert.Equal("Enter command JSON (blank to return to menu):", await cli.ReadLineAsync(host.Token));
        Assert.True((await cli.ReadAsync<CommandResult>(host.Token)).Success);
        Assert.Equal("Enter command JSON (blank to return to menu):", await cli.ReadLineAsync(host.Token));
        await ReadMenuAsync(cli, host.Token);
        Assert.Equal(0, await cli.ExitAsync(host.Token));
        var errors = (await cli.Errors.WaitAsync(host.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, errors.Length);
        using var usage = JsonDocument.Parse(errors[0]);
        using var rejection = JsonDocument.Parse(errors[1]);
        Assert.Equal("usage", usage.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("invalid_argument", rejection.RootElement.GetProperty("errorCode").GetString());
        Assert.False(host.Engine.Current!.Paused);
    }

    [Fact]
    public async Task InteractiveStatusSubscribesAndReturnsToMenuAfterDisconnect()
    {
        await using var host = await PipeHost.StartAsync();
        await using var cli = CliProcess.Start();
        await cli.SendAsync($"1\n{host.Name}\n0\n", host.Token);
        await ReadMenuAsync(cli, host.Token);
        Assert.Equal("Pipe name (blank for default):", await cli.ReadLineAsync(host.Token));
        Assert.Equal("Current Workspace: test", await cli.ReadLineAsync(host.Token));
        Assert.Equal("Current Window: None", await cli.ReadLineAsync(host.Token));
        Assert.Equal("Current Config direction: Not exposed by host", await cli.ReadLineAsync(host.Token));
        Assert.StartsWith("Latest event: snapshot ", await cli.ReadLineAsync(host.Token));
        await host.Server.DisposeAsync();
        await ReadMenuAsync(cli, host.Token);
        Assert.Equal(0, await cli.ExitAsync(host.Token));
        using var error = JsonDocument.Parse(await cli.Errors.WaitAsync(host.Token));
        Assert.Equal("connection_error", error.RootElement.GetProperty("errorCode").GetString());
    }

    private static async Task ReadMenuAsync(CliProcess cli, CancellationToken token)
    {
        Assert.Equal("WinManager CLI", await cli.ReadLineAsync(token));
        Assert.Equal("1. Status observer", await cli.ReadLineAsync(token));
        Assert.Equal("2. Send commands", await cli.ReadLineAsync(token));
        Assert.Equal("3. Help", await cli.ReadLineAsync(token));
        Assert.Equal("0. Exit", await cli.ReadLineAsync(token));
        Assert.Equal("Select an option:", await cli.ReadLineAsync(token));
    }

    [Fact]
    public async Task ExecutableStatusSubscribesAndUpdatesFocusedWindowAndLatestEvent()
    {
        await using var host = await PipeHost.StartAsync();
        await using var status = CliProcess.Start("status", "--pipe", host.Name);
        Assert.Equal("Current Workspace: test", await status.ReadLineAsync(host.Token));
        Assert.Equal("Current Window: None", await status.ReadLineAsync(host.Token));
        Assert.Equal("Current Config direction: Not exposed by host", await status.ReadLineAsync(host.Token));
        Assert.StartsWith("Latest event: snapshot ", await status.ReadLineAsync(host.Token));

        await using var client = await WinManagerClient.ConnectAsync(host.Name, cancellationToken: host.Token);
        var window = host.Engine.Current!.Desktop.Windows.Single();
        Assert.True((await client.ExecuteAsync(new(CommandKind.Focus, WindowId: window.Id), host.Token)).Success);
        Assert.Equal("Current Workspace: test", await status.ReadLineAsync(host.Token));
        Assert.Equal($"Current Window: {window.Title} [test] ({window.Id})", await status.ReadLineAsync(host.Token));
        Assert.Equal("Current Config direction: Not exposed by host", await status.ReadLineAsync(host.Token));
        Assert.StartsWith("Latest event: focus.changed ", await status.ReadLineAsync(host.Token));

        Assert.True((await client.ExecuteAsync(new(CommandKind.Pause, Paused: true), host.Token)).Success);
        Assert.Equal("Current Workspace: test", await status.ReadLineAsync(host.Token));
        Assert.Contains(window.Title, (await status.ReadLineAsync(host.Token))!);
        Assert.Equal("Current Config direction: Not exposed by host", await status.ReadLineAsync(host.Token));
        var latest = await status.ReadLineAsync(host.Token);
        Assert.StartsWith("Latest event: pause.changed ", latest);
        Assert.Contains("paused", latest!);
        await host.Server.DisposeAsync();
        Assert.Equal(4, await status.ExitAsync(host.Token));
        using var error = JsonDocument.Parse(await status.Errors.WaitAsync(host.Token));
        Assert.Equal("connection_error", error.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ExecutableSnapshotWatchStdinCommandUnsubscribeAndShutdown()
    {
        await using var host = await PipeHost.StartAsync();
        await using (var snapshotProcess = CliProcess.Start("snapshot", "--pipe", host.Name))
        {
            var snapshot = await snapshotProcess.ReadAsync<ManagerSnapshot>(host.Token);
            Assert.Equal(host.Engine.Current!.InstanceId, snapshot.InstanceId);
            Assert.False(snapshot.Paused);
            Assert.Equal(0, await snapshotProcess.ExitAsync(host.Token));
            Assert.Equal("", await snapshotProcess.Errors.WaitAsync(host.Token));
            Assert.Null(await snapshotProcess.ReadLineAsync(host.Token));
        }

        await using var watch = CliProcess.Start("watch", "--pipe", host.Name, "--event-types", "pause.changed");
        var initial = await watch.ReadAsync<EventEnvelope>(host.Token);
        Assert.Equal("snapshot", initial.EventType);
        Assert.Equal(host.Engine.Current!.InstanceId, initial.InstanceId);
        Assert.False(initial.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);

        await using var client = await WinManagerClient.ConnectAsync(host.Name, cancellationToken: host.Token);
        await using var subscription = await client.SubscribeAsync(["pause.changed"], host.Token);
        await using var events = subscription.ReadEventsAsync(host.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("snapshot", events.Current.EventType);

        await using (var command = CliProcess.Start("command", "-", "--pipe", host.Name))
        {
            await command.SendAsync("{\"kind\":\"pause\",\"paused\":true}", host.Token);
            var result = await command.ReadAsync<CommandResult>(host.Token);
            Assert.True(result.Success);
            Assert.Equal(0, await command.ExitAsync(host.Token));
            Assert.Equal("", await command.Errors.WaitAsync(host.Token));
            Assert.Null(await command.ReadLineAsync(host.Token));

            var changed = await watch.ReadAsync<EventEnvelope>(host.Token);
            Assert.Equal("pause.changed", changed.EventType);
            Assert.Equal(initial.InstanceId, changed.InstanceId);
            Assert.True(changed.Sequence > initial.Sequence);
            Assert.True(changed.StateRevision > initial.StateRevision);
            Assert.Equal(result.StateRevision, changed.StateRevision);
            Assert.True(changed.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
            Assert.True(await events.MoveNextAsync());
            Assert.Equal(changed.Sequence, events.Current.Sequence);
        }

        await subscription.UnsubscribeAsync(host.Token);
        Assert.False(await events.MoveNextAsync());
        Assert.True((await client.ExecuteAsync(new(CommandKind.Pause, Paused: false), host.Token)).Success);
        var resumed = await watch.ReadAsync<EventEnvelope>(host.Token);
        Assert.Equal("pause.changed", resumed.EventType);
        Assert.True(resumed.Sequence > initial.Sequence);
        Assert.False(resumed.Payload.Deserialize<ManagerSnapshot>(Protocol.JsonOptions)!.Paused);
        Assert.True((await client.ExecuteAsync(new(CommandKind.Exit), host.Token)).Success);
        await host.EngineRun.WaitAsync(host.Token);
        await host.Server.DisposeAsync();
        await host.ServerRun.WaitAsync(host.Token);
        Assert.Equal(4, await watch.ExitAsync(host.Token));
        Assert.Null(await watch.ReadLineAsync(host.Token));
        using var error = JsonDocument.Parse(await watch.Errors.WaitAsync(host.Token));
        Assert.Equal("connection_error", error.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("{", 2, "usage")]
    [InlineData("null", 2, "usage")]
    [InlineData("{}", 2, "usage")]
    [InlineData("{\"kind\":\"unknown\"}", 2, "usage")]
    [InlineData("{\"kind\":\"pause\",\"paused\":true,\"extra\":true}", 2, "usage")]
    [InlineData("{\"kind\":\"pause\"}", 3, "invalid_argument")]
    [InlineData("{\"kind\":\"focus\",\"windowId\":\"00000000-0000-0000-0000-000000000001\"}", 3, "not_found")]
    public async Task ExecutableRejectsMalformedJsonAndRejectedCommandsWithoutMutation(string json, int exitCode, string errorCode)
    {
        await using var host = await PipeHost.StartAsync();
        var before = host.Engine.Current!;
        await using var command = CliProcess.Start("command", "-", "--pipe", host.Name);
        await command.SendAsync(json, host.Token);
        Assert.Equal(exitCode, await command.ExitAsync(host.Token));
        Assert.Null(await command.ReadLineAsync(host.Token));
        using var error = JsonDocument.Parse(await command.Errors.WaitAsync(host.Token));
        Assert.Equal(errorCode, error.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("message").GetString()));
        Assert.Equal(before.StateRevision, host.Engine.Current!.StateRevision);
        Assert.False(host.Engine.Current.Paused);
        await using var client = await WinManagerClient.ConnectAsync(host.Name, cancellationToken: host.Token);
        Assert.Equal(before.InstanceId, (await client.QuerySnapshotAsync(host.Token)).InstanceId);
    }

    private sealed class CliProcess : IAsyncDisposable
    {
        private readonly Process process;
        public Task<string> Errors { get; }

        private CliProcess(Process process)
        {
            this.process = process;
            Errors = process.StandardError.ReadToEndAsync();
        }

        public static CliProcess Start(params string[] arguments)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "cli", "WinManager.Cli.exe");
            Assert.True(File.Exists(executable), $"CLI executable was not built and copied: {executable}");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return new CliProcess(Process.Start(start) ?? throw new InvalidOperationException("CLI did not start."));
        }

        public async Task SendAsync(string json, CancellationToken token)
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), token);
            process.StandardInput.Close();
        }

        public async Task<T> ReadAsync<T>(CancellationToken token)
        {
            var line = await ReadLineAsync(token);
            Assert.NotNull(line);
            return JsonSerializer.Deserialize<T>(line, Protocol.JsonOptions)!;
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken token) => process.StandardOutput.ReadLineAsync(token);

        public async Task<int> ExitAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Errors.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { process.Dispose(); }
        }
    }
}