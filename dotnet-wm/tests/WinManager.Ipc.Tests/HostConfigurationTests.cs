using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinManager.Contracts;
using WinManager.Host;
using WinManager.Windows;
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
        Assert.All(loaded.Workspaces, workspace => Assert.Equal(WorkspaceLayout.Layered, workspace.Layout));
        Assert.Equal(1, LayeredLayoutOptions.Default.WindowWidthRatio);
        Assert.Equal(32, LayeredLayoutOptions.Default.HorizontalOffset);
        Assert.True(loaded.Touchpad.Enabled);
        using var serialized = JsonDocument.Parse(ManagerConfiguration.Serialize(loaded));
        Assert.True(serialized.RootElement.GetProperty("touchpad").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void TouchpadConfigurationDefaultsOnForLegacyFilesAndCanBeDisabledExplicitly()
    {
        var legacy = ManagerConfiguration.Parse("""{"workspaces":[{"name":"Old"}]}""");
        var disabled = ManagerConfiguration.Parse("""{"touchpad":{"enabled":false}}""");

        Assert.True(legacy.Touchpad.Enabled);
        Assert.False(disabled.Touchpad.Enabled);
        Assert.Throws<InvalidDataException>(() => ManagerConfiguration.Parse("""{"touchpad":null}"""));
        Assert.Throws<JsonException>(() => ManagerConfiguration.Parse("""{"touchpad":{"enabled":true,"unknown":1}}"""));
    }

    [Theory]
    [InlineData(TouchpadSwipeDirection.Left, Direction.Right)]
    [InlineData(TouchpadSwipeDirection.Right, Direction.Left)]
    public void ThreeFingerHorizontalSwipesInvertDirectionalFocus(TouchpadSwipeDirection physical,
        Direction expected)
    {
        var command = TouchpadGesturePolicy.CreateCommand(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 3, physical, 0, 100), AvailableTouchpad());

        Assert.Equal(new ManagerCommand(CommandKind.Focus, Direction: expected), command);
    }

    [Theory]
    [InlineData(TouchpadSwipeDirection.Left, Direction.Left)]
    [InlineData(TouchpadSwipeDirection.Right, Direction.Right)]
    [InlineData(TouchpadSwipeDirection.Up, Direction.Up)]
    [InlineData(TouchpadSwipeDirection.Down, Direction.Down)]
    public void FourFingerSwipesMoveSpatiallyInTheirPhysicalDirection(TouchpadSwipeDirection physical,
        Direction expected)
    {
        var command = TouchpadGesturePolicy.CreateCommand(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 4, physical, 0, 100), AvailableTouchpad());

        Assert.Equal(new ManagerCommand(CommandKind.MoveSpatial, Direction: expected), command);
    }

    [Fact]
    public void VerticalThreeFingerAndContinuousGesturesDoNotBecomeHostCommands()
    {
        Assert.Null(TouchpadGesturePolicy.CreateCommand(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 3, TouchpadSwipeDirection.Up, 0, 100),
            AvailableTouchpad()));
        Assert.Null(TouchpadGesturePolicy.CreateCommand(
            new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 2, null, 0.1, 100), AvailableTouchpad()));
    }

    [Fact]
    public void HostGesturePolicyBlocksActionsUntilExclusiveOwnershipIsVerified()
    {
        var blocked = new TouchpadCapabilityResult(TouchpadCapabilityState.GestureConflictBlocked, 1, true, false,
            "unverified");

        Assert.Null(TouchpadGesturePolicy.CreateCommand(
            new TouchpadGesture(TouchpadGestureKind.Swipe, 3, TouchpadSwipeDirection.Left, 0, 100), blocked));
    }

    private static TouchpadCapabilityResult AvailableTouchpad() =>
        new(TouchpadCapabilityState.Available, 1, true, true, null);

    [Fact]
    public void GeneratedConfigurationMetadataRoundTripsNestedWorkspaceSettings()
    {
        var configuration = new ManagerConfiguration
        {
            Workspaces =
            [
                new("Research", Layout: WorkspaceLayout.Layered,
                    Position: new DeskPosition(-125, 64),
                    LayeredLayoutOptions: new LayeredLayoutOptions(0.75, 48))
            ]
        };

        var json = ManagerConfiguration.Serialize(configuration);
        var loaded = ManagerConfiguration.Parse(json);

        Assert.Equal(configuration.Workspaces, loaded.Workspaces);
        Assert.Contains("\"layout\": \"layered\"", json);

        var sparse = ManagerConfiguration.Parse("""{"workspaces":[{"name":"Only"}]}""");
        Assert.Equal(256, sparse.InputCapacity);
        Assert.Equal(16, sparse.MaximumClients);
        Assert.Equal(128, sparse.OutputCapacity);
        Assert.Equal(WorkspaceLayout.Layered, Assert.Single(sparse.Workspaces).Layout);
    }

    [Fact]
    public void ConfigurationStoreSavesValidatedContentAndRejectsStaleOrInvalidEdits()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ConfigurationStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            var initial = new ManagerConfiguration();
            File.WriteAllText(path, ManagerConfiguration.Serialize(initial));
            var store = new ConfigurationStore(path);
            var document = store.Read();
            Assert.True(document.IsValid);
            Assert.Equal(Path.GetFullPath(path), document.Path);

            var changedContent = ManagerConfiguration.Serialize(initial with { InnerGap = 19, OuterGap = 23 });
            var saved = store.Save(document.Revision, changedContent);
            Assert.True(saved.IsValid);
            Assert.Equal(19, ManagerConfiguration.Load(path, createDefault: false).InnerGap);
            Assert.Equal(23, ManagerConfiguration.Load(path, createDefault: false).OuterGap);

            Assert.Throws<ConfigurationConflictException>(() => store.Save(document.Revision, changedContent));
            var current = store.Read();
            var beforeInvalidSave = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => store.Save(current.Revision, "{\"workspaces\":[]}"));
            Assert.Throws<JsonException>(() => store.Save(current.Revision, "{\"unknownSetting\":true}"));
            Assert.Throws<InvalidDataException>(() =>
                store.Save(current.Revision, "{\"innerGap\":8,\"INNERGAP\":9}"));
            Assert.Equal(beforeInvalidSave, File.ReadAllText(path));

            File.WriteAllText(path, ManagerConfiguration.Serialize(initial with { InnerGap = 21 }));
            Assert.Throws<ConfigurationConflictException>(() => store.Save(current.Revision, changedContent));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationStoreReturnsInvalidDiskTextForRepairAndCanReplaceIt()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ConfigurationRepairTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            const string invalid = "{ \"unknown\": true }";
            File.WriteAllText(path, invalid);
            var store = new ConfigurationStore(path);
            var read = store.Read();
            Assert.False(read.IsValid);
            Assert.Equal(invalid, read.Content);
            Assert.NotNull(read.ValidationError);

            var repaired = store.Save(read.Revision, ManagerConfiguration.Serialize(new ManagerConfiguration()));
            Assert.True(repaired.IsValid);
            Assert.True(ManagerConfiguration.Load(path, createDefault: false) is not null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationStoreBoundsIpcReadsAndWrites()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ConfigurationSizeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, ManagerConfiguration.Serialize(new ManagerConfiguration()));
            var store = new ConfigurationStore(path);
            var original = store.Read();
            var oversizedContent = new string(' ', ConfigurationStore.MaximumIpcConfigurationBytes + 1);
            Assert.Throws<InvalidDataException>(() => store.Save(original.Revision, oversizedContent));
            Assert.Equal(original.Content, File.ReadAllText(path));

            File.WriteAllText(path, new string(' ', ConfigurationStore.MaximumIpcConfigurationBytes + 1));
            Assert.Throws<InvalidDataException>(() => store.Read());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceLayoutDefaultsToLayeredWhenOmittedButAcceptsExplicitSplit()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(file,
            "{\"workspaces\":[{\"name\":\"Layered\"},{\"name\":\"Split\",\"layout\":\"split\"}]}");

        var loaded = ManagerConfiguration.Load(file, createDefault: false);

        Assert.Equal(WorkspaceLayout.Layered,
            loaded.Workspaces.Single(workspace => workspace.Name == "Layered").Layout);
        Assert.Equal(WorkspaceLayout.Split, loaded.Workspaces.Single(workspace => workspace.Name == "Split").Layout);
    }

    [Fact]
    public void DefaultShortcutSetContainsMonitorWorkspaceAndGestureBindingsAndCustomCanOverride()
    {
        var defaults = new ManagerConfiguration().BuildShortcutBindings();
        Assert.Equal(29, defaults.Count);
        Assert.Contains(defaults, binding => binding.Command.Kind == CommandKind.FocusMonitor &&
                                             binding.Command.MonitorNumber == 1 && binding.VirtualKey == 0x31);
        Assert.Contains(defaults, binding => binding.Command.Kind == CommandKind.MoveToWorkspace &&
                                             binding.Command.WorkspaceNumber == 1 && binding.VirtualKey == 0x31 &&
                                             (binding.Modifiers & 0xF) == 5);
        Assert.Contains(defaults, binding => binding.Command.Kind == CommandKind.Focus &&
                                             binding.Command.Direction == Direction.Left && binding.VirtualKey == 0x25);
        Assert.Contains(defaults, binding => binding.Command.Kind == CommandKind.MoveSpatial &&
                                             binding.Command.Direction == Direction.Left &&
                                             (binding.Modifiers & 0xF) == 7);
        var vimBindings = new (Direction Direction, uint Key)[]
        {
            (Direction.Left, 0x48), (Direction.Down, 0x4A),
            (Direction.Up, 0x4B), (Direction.Right, 0x4C)
        };
        foreach (var (direction, key) in vimBindings)
        {
            Assert.Contains(defaults, binding => binding.Command.Kind == CommandKind.Focus &&
                                                 binding.Command.Direction == direction &&
                                                 binding.VirtualKey == key && (binding.Modifiers & 0xF) == 1);
            Assert.Contains(defaults, binding => binding.Command.Kind == CommandKind.MoveSpatial &&
                                                 binding.Command.Direction == direction &&
                                                 binding.VirtualKey == key && (binding.Modifiers & 0xF) == 5);
        }

        Assert.Equal(defaults.Count, defaults.Select(binding => (binding.Modifiers & 0xF, binding.VirtualKey))
            .Distinct().Count());

        var custom = new ShortcutBinding(1, 0x31, new(CommandKind.Pause, Paused: true));
        var overridden = new ManagerConfiguration { Shortcuts = [custom] }.BuildShortcutBindings();
        Assert.Equal(defaults.Count, overridden.Count);
        Assert.Equal(custom, Assert.Single(overridden, binding => binding.VirtualKey == 0x31 &&
                                                                  (binding.Modifiers & 0xF) == 1));
        var vimOverride = new ShortcutBinding(1, 0x48, new(CommandKind.Redraw));
        var overriddenVim = new ManagerConfiguration { Shortcuts = [vimOverride] }.BuildShortcutBindings();
        Assert.Equal(defaults.Count, overriddenVim.Count);
        Assert.Equal(vimOverride, Assert.Single(overriddenVim, binding => binding.VirtualKey == 0x48 &&
                                                                          (binding.Modifiers & 0xF) == 1));
        Assert.Empty(new ManagerConfiguration { EnableDefaultShortcuts = false }.BuildShortcutBindings());
    }

    [Fact]
    public void MonitorOrderAndNumberedCommandsValidateAndRoundTrip()
    {
        var config = new ManagerConfiguration
        {
            Workspaces = [new("One"), new("Two")],
            MonitorOrder = [@"\\.\DISPLAY2", @"\\.\DISPLAY1"],
            Shortcuts = [new(5, 0x32, new(CommandKind.MoveToWorkspace, WorkspaceNumber: 2))]
        };
        config.Validate();
        var roundTrip = ManagerConfiguration.Parse(ManagerConfiguration.Serialize(config));
        Assert.Equal(config.MonitorOrder, roundTrip.MonitorOrder);
        Assert.Equal(config.Shortcuts, roundTrip.Shortcuts);
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            MonitorOrder = ["display", "DISPLAY"]
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Workspaces = [new("One")],
            Shortcuts = [new(5, 0x32, new(CommandKind.MoveToWorkspace, WorkspaceNumber: 2))]
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Shortcuts = [new(1, 0x31, new(CommandKind.FocusMonitor, MonitorNumber: 0))]
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Shortcuts = [new(1, 0x31, new(CommandKind.MoveSpatial))]
        }).Validate());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"innerGap\":-1}")]
    [InlineData("{\"outerGap\":300}")]
    [InlineData("{\"innerGap\":1,\"InnerGap\":2}")]
    [InlineData("{\"workspaces\":[{\"name\":\"one\"},{\"name\":\"ONE\"}]}")]
    [InlineData("{\"workspaces\":[{\"name\":\"one\",\"layout\":\"unknown\"}]}")]
    [InlineData("{\"workspaces\":[{\"name\":\"one\",\"position\":{\"x\":100001,\"y\":0}}]}")]
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
    public void DeskPositionsRoundTripInWorkspaceConfigurationAndPositionShortcutsValidate()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "config.json");
        var position = new DeskPosition(-160, 240);
        var config = new ManagerConfiguration
        {
            Workspaces = [new("One", Position: position)],
            Shortcuts =
            [
                new(2, 65, new(CommandKind.SetWorkspacePosition,
                    WorkspaceName: "One", Position: position))
            ]
        };

        config.Validate();
        var json = ManagerConfiguration.Serialize(config);
        using var document = JsonDocument.Parse(json);
        var serializedPosition = document.RootElement.GetProperty("workspaces")[0].GetProperty("position");
        Assert.Equal(2, serializedPosition.EnumerateObject().Count());
        File.WriteAllText(file, json);
        var loaded = ManagerConfiguration.Load(file, createDefault: false);

        Assert.Equal(position, Assert.Single(loaded.Workspaces).Position);
        Assert.Equal(position, Assert.Single(loaded.Shortcuts).Command.Position);
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Shortcuts =
            [
                new(2, 65, new(CommandKind.SetWorkspacePosition,
                    WorkspaceName: "One", Position: new(double.PositiveInfinity, 0)))
            ]
        }).Validate());
    }

    [Fact]
    public void ToggleModeShortcutRequiresNoModeOrWindowId()
    {
        var config = ManagerConfiguration.Parse(
            """{"shortcuts":[{"modifiers":16390,"virtualKey":84,"command":{"kind":"toggleMode"}}]}""");
        config.Validate();
        var command = Assert.Single(config.Shortcuts).Command;
        Assert.Equal(CommandKind.ToggleMode, command.Kind);
        Assert.Null(command.WindowId);
        Assert.Null(command.Mode);
    }

    [Fact]
    public void FocusCyclingShortcutsValidateDirectionFiltersAndWorkspaceScope()
    {
        var valid = new ManagerCommand[]
        {
            new(CommandKind.FocusCycle),
            new(CommandKind.FocusCycle, Mode: WindowMode.Floating, CycleDirection: FocusCycleDirection.Previous),
            new(CommandKind.FocusCycle, WorkspaceName: "2"),
            new(CommandKind.FocusCycle, WorkspaceId: Guid.NewGuid())
        };
        foreach (var command in valid)
            (new ManagerConfiguration { Shortcuts = [new(2, 65, command)] }).Validate();
        var invalid = new ManagerCommand[]
        {
            new(CommandKind.FocusCycle, CycleDirection: (FocusCycleDirection)99),
            new(CommandKind.FocusCycle, Direction: Direction.Left),
            new(CommandKind.FocusCycle, Mode: (WindowMode)99),
            new(CommandKind.FocusCycle, WorkspaceName: "unknown"),
            new(CommandKind.FocusCycle, WorkspaceId: Guid.Empty),
            new(CommandKind.Redraw, CycleDirection: FocusCycleDirection.Next)
        };
        foreach (var command in invalid)
            Assert.Throws<InvalidDataException>(() =>
                (new ManagerConfiguration { Shortcuts = [new(2, 65, command)] }).Validate());
    }

    [Fact]
    public void WorkspaceLayoutDefaultsRoundTripAndShortcutsValidateTargets()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(file, """
                                {
                                  "workspaces": [{ "name": "Layered", "layout": "layered",
                                    "layeredLayoutOptions": { "windowWidthRatio": 0.65, "horizontalOffset": 48 } }],
                                  "shortcuts": [{ "modifiers": 2, "virtualKey": 76,
                                    "command": { "kind": "setWorkspaceLayout", "workspaceName": "Layered", "layout": "layered",
                                      "layeredLayoutOptions": { "windowWidthRatio": 0.6, "horizontalOffset": 0 } } }]
                                }
                                """);

        var loaded = ManagerConfiguration.Load(file, createDefault: false);

        Assert.Equal(WorkspaceLayout.Layered, Assert.Single(loaded.Workspaces).Layout);
        Assert.Equal(new LayeredLayoutOptions(0.65, 48),
            Assert.Single(loaded.Workspaces).LayeredLayoutOptions);
        var shortcut = Assert.Single(loaded.Shortcuts).Command;
        Assert.Equal(CommandKind.SetWorkspaceLayout, shortcut.Kind);
        Assert.Equal(WorkspaceLayout.Layered, shortcut.Layout);
        Assert.Equal("Layered", shortcut.WorkspaceName);
        Assert.Equal(new LayeredLayoutOptions(0.6, 0), shortcut.LayeredLayoutOptions);

        var invalid = new ManagerCommand[]
        {
            new(CommandKind.SetWorkspaceLayout, WorkspaceName: "Layered"),
            new(CommandKind.SetWorkspaceLayout, WorkspaceName: "missing", Layout: WorkspaceLayout.Layered),
            new(CommandKind.SetWorkspaceLayout, WorkspaceName: "Layered", Layout: (WorkspaceLayout)99),
            new(CommandKind.SetWorkspaceLayout, WorkspaceId: Guid.Empty, Layout: WorkspaceLayout.Layered),
            new(CommandKind.SetWorkspaceLayout, WorkspaceName: "Layered", Layout: WorkspaceLayout.Layered,
                Mode: WindowMode.Tiling),
            new(CommandKind.SetWorkspaceLayout, WorkspaceName: "Layered", Layout: WorkspaceLayout.Layered,
                LayeredLayoutOptions: new LayeredLayoutOptions(double.NaN, 0)),
            new(CommandKind.Pause, Paused: true, LayeredLayoutOptions: LayeredLayoutOptions.Default),
            new(CommandKind.Redraw, Layout: WorkspaceLayout.Layered)
        };
        foreach (var command in invalid)
            Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
            {
                Shortcuts = [new(2, 76, command)]
            }).Validate());

        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Workspaces = [new("Layered", LayeredLayoutOptions: new LayeredLayoutOptions(0, 32))]
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (new ManagerConfiguration
        {
            Workspaces = [new("Layered", LayeredLayoutOptions: new LayeredLayoutOptions(0.8, 257))]
        }).Validate());
    }

    [Fact]
    public void ContainerNavigationShortcutsAreValidated()
    {
        (new ManagerConfiguration
        {
            Shortcuts = [new(2, 65, new(CommandKind.SelectContainer, Navigation: ContainerNavigation.Parent))]
        }).Validate();
        var invalid = new ManagerCommand[]
        {
            new(CommandKind.SelectContainer, Navigation: (ContainerNavigation)99),
            new(CommandKind.SelectContainer, NodeId: Guid.NewGuid(), Navigation: ContainerNavigation.Parent),
            new(CommandKind.SelectContainer, NodeId: Guid.Empty),
            new(CommandKind.Redraw, Navigation: ContainerNavigation.Parent)
        };
        foreach (var command in invalid)
            Assert.Throws<InvalidDataException>(() =>
                (new ManagerConfiguration { Shortcuts = [new(2, 65, command)] }).Validate());
    }

    [Fact]
    public void DuplicateShortcutsAndUnknownWorkspaceCommandAreRejected()
    {
        var shortcut = new ShortcutBinding(2, 65, new(CommandKind.Redraw));
        Assert.Throws<InvalidDataException>(() =>
            (new ManagerConfiguration { Shortcuts = [shortcut, shortcut] }).Validate());
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
    public void ActivationShortcutAcceptsOnlyAValidOptionalMonitorTarget()
    {
        (new ManagerConfiguration
        {
            Shortcuts =
            [
                new(2, 65, new(CommandKind.ActivateWorkspace,
                    WorkspaceName: "2", MonitorId: Guid.NewGuid()))
            ]
        }).Validate();
        var invalid = new ManagerCommand[]
        {
            new(CommandKind.ActivateWorkspace, WorkspaceName: "2", MonitorId: Guid.Empty),
            new(CommandKind.Pause, Paused: true, MonitorId: Guid.NewGuid())
        };
        foreach (var command in invalid)
            Assert.Throws<InvalidDataException>(() =>
                (new ManagerConfiguration { Shortcuts = [new(2, 65, command)] }).Validate());
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
    public async Task HostTrayQuitRequestsGracefulShutdownAndStopsTrayLoop()
    {
        var lifetime = new TestHostApplicationLifetime();
        var tray = new FakeHostTray();
        using var service = new HostTrayService(lifetime, tray);

        await service.StartAsync(CancellationToken.None);
        await tray.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tray.RequestQuit();

        Assert.Equal(1, lifetime.StopRequests);
        await service.StopAsync(CancellationToken.None);
        Assert.True(tray.StopObserved);
    }

    [Fact]
    public void HostRecoveryLaunchUsesSelfExecutableOnlyForAotDefault()
    {
        var aot = HostRecoveryLaunch.Resolve(null, @"C:\Apps\WinManager.Host.exe", useSelfExecutable: true);
        Assert.Equal(@"C:\Apps\WinManager.Host.exe", aot.Executable);
        Assert.Equal(["--recovery"], aot.ArgumentPrefix);

        var release = HostRecoveryLaunch.Resolve(null, @"C:\Apps\WinManager.Host.exe", useSelfExecutable: false);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "WinManager.Recovery.exe"), release.Executable);
        Assert.Empty(release.ArgumentPrefix);

        var configured = HostRecoveryLaunch.Resolve(@"D:\Recovery\WinManager.Recovery.exe",
            @"C:\Apps\WinManager.Host.exe", useSelfExecutable: true);
        Assert.Equal(@"D:\Recovery\WinManager.Recovery.exe", configured.Executable);
        Assert.Empty(configured.ArgumentPrefix);
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
        public string Path { get; } =
            System.IO.Path.Combine(AppContext.BaseDirectory, "HostTestData", Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakeHostTray : IHostTray
    {
        private Action? requestShutdown;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StopObserved { get; private set; }

        public async Task RunAsync(Action stopApplication, CancellationToken stoppingToken)
        {
            requestShutdown = stopApplication;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                StopObserved = true;
            }
        }

        public void RequestQuit() => requestShutdown!();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public int StopRequests { get; private set; }

        public void StopApplication()
        {
            StopRequests++;
            stopping.Cancel();
        }
    }
}