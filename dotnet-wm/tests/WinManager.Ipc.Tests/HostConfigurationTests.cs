using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinManager.Contracts;
using WinManager.Host;
using Xunit;

namespace WinManager.Ipc.Tests;

public sealed class HostConfigurationTests
{
    [Fact]
    public void DefaultsAreValidAndConfigurationRoundTrips()
    {
        var config = new ManagerConfiguration();
        config.Validate();
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "config.json");
        var loaded = ManagerConfiguration.Load(file, createDefault: true);
        Assert.Equal(config.Workspaces.ToArray(), loaded.Workspaces.ToArray());
        Assert.Equal(8, loaded.InnerGap);
        Assert.Empty(loaded.Shortcuts);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"innerGap\":-1}")]
    [InlineData("{\"outerGap\":300}")]
    [InlineData("{\"innerGap\":1,\"InnerGap\":2}")]
    [InlineData("{\"workspaces\":[{\"name\":\"one\"},{\"name\":\"ONE\"}]}")]
    [InlineData("{\"workspaces\":null}")]
    [InlineData("{\"ignoredProcessNames\":[\"\"]}")]
    [InlineData("{\"shortcuts\":[{\"modifiers\":16,\"virtualKey\":65,\"command\":{\"kind\":\"redraw\"}}]}")]
    [InlineData("{\"shortcuts\":[{\"modifiers\":2,\"virtualKey\":65,\"command\":{\"kind\":\"resize\"}}]}")]
    public void InvalidJsonConfigurationIsRejected(string json)
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(file, json);
        var error = Record.Exception(() => ManagerConfiguration.Load(file, createDefault: false));
        Assert.NotNull(error);
        Assert.True(error is JsonException or InvalidDataException);
    }

    [Fact]
    public void ToggleModeShortcutRequiresNoModeOrWindowId()
    {
        var config = JsonSerializer.Deserialize<ManagerConfiguration>(
            """{"shortcuts":[{"modifiers":16390,"virtualKey":84,"command":{"kind":"toggleMode"}}]}""", Protocol.JsonOptions)!;
        config.Validate();
        var command = Assert.Single(config.Shortcuts).Command;
        Assert.Equal(CommandKind.ToggleMode, command.Kind);
        Assert.Null(command.WindowId);
        Assert.Null(command.Mode);
    }

    [Fact]
    public void DuplicateShortcutsAndUnknownWorkspaceCommandAreRejected()
    {
        var shortcut = new ShortcutBinding(2, 65, new(CommandKind.Redraw));
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration { Shortcuts = [shortcut, shortcut] }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Shortcuts = [new(2, 65, new(CommandKind.ActivateWorkspace, WorkspaceName: "unknown"))]
        }).Validate());
        (new ManagerConfiguration
        {
            Shortcuts = [new(2, 65, new(CommandKind.ActivateWorkspace, WorkspaceName: "2"))]
        }).Validate();
    }

    [Fact]
    public async Task SingleInstanceLeaseSurvivesCrossThreadDisposalAndCanBeReacquired()
    {
        var name = "Local\\WinManager.LeaseTest." + Guid.NewGuid().ToString("N");
        var first = new SingleInstanceLease(name);
        Assert.True(first.Acquired);
        using (var second = new SingleInstanceLease(name)) Assert.False(second.Acquired);
        await Task.Run(first.Dispose);
        using var replacement = new SingleInstanceLease(name);
        Assert.True(replacement.Acquired);
    }

    [Fact]
    public void LogsRotateAndBoundLargeMessages()
    {
        using var directory = new TestDirectory();
        using (var provider = new RotatingFileLoggerProvider(directory.Path, 1024, 3))
        {
            var logger = provider.CreateLogger("test");
            for (var index = 0; index < 40; index++) logger.LogInformation("{Message}", new string('a', 180));
            logger.LogError("{Message}", new string('x', 10000));
        }
        var files = Directory.GetFiles(directory.Path);
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        Assert.Contains("truncated", File.ReadAllText(Path.Combine(directory.Path, "manager.log")));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "HostTestData", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}