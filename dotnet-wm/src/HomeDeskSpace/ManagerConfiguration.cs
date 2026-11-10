using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WinManager.Contracts;

namespace WinManager.Host;

public sealed record ManagerConfiguration
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(Protocol.JsonOptions)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ManagerConfigurationJsonContext.Default,
                Protocol.JsonOptions.TypeInfoResolver!)
        };
        options.MakeReadOnly();
        return options;
    }

    public IReadOnlyList<WorkspaceDefinition> Workspaces { get; init; } = [new("1"), new("2"), new("3")];
    public int InnerGap { get; init; } = 8;
    public int OuterGap { get; init; } = 8;
    public IReadOnlyList<string> IgnoredProcessNames { get; init; } = [];
    public IReadOnlyList<string> IgnoredClassNames { get; init; } = [];
    public IReadOnlyList<ShortcutBinding> Shortcuts { get; init; } = [];
    public bool EnableDefaultShortcuts { get; init; } = true;
    public TouchpadOptions Touchpad { get; init; } = new();
    public IReadOnlyList<string> MonitorOrder { get; init; } = [];
    public int InputCapacity { get; init; } = 256;
    public int MaximumClients { get; init; } = 16;
    public int OutputCapacity { get; init; } = 128;
    public int LogMaximumBytes { get; init; } = 2 * 1024 * 1024;
    public int LogFileCount { get; init; } = 4;

    public void Validate()
    {
        if (InnerGap is < 0 or > 256 || OuterGap is < 0 or > 256)
            throw new InvalidDataException("Gaps must be between 0 and 256 DIPs.");
        if (InputCapacity is < 1 or > 4096 || MaximumClients is < 1 or > 64 || OutputCapacity is < 8 or > 1024)
            throw new InvalidDataException("Invalid queue or client limits.");
        if (LogMaximumBytes is < 65536 or > 20971520 || LogFileCount is < 2 or > 10)
            throw new InvalidDataException("Invalid log rotation limits.");
        if (Workspaces is null || Workspaces.Count is < 1 or > 128 ||
            Workspaces.Any(workspace => workspace is null || string.IsNullOrWhiteSpace(workspace.Name) ||
                                        workspace.Name.Length > 64 ||
                                        workspace.MonitorDeviceName is { Length: > 256 } ||
                                        !Enum.IsDefined(workspace.Layout) ||
                                        workspace.Position is { IsValid: false } ||
                                        workspace.LayeredLayoutOptions is { IsValid: false }) ||
            Workspaces.Select(workspace => workspace.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            Workspaces.Count)
            throw new InvalidDataException("Workspace names must be nonblank and unique.");
        ValidateIgnores(IgnoredProcessNames);
        ValidateIgnores(IgnoredClassNames);
        if (MonitorOrder is null || MonitorOrder.Count > 10 ||
            MonitorOrder.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256) ||
            MonitorOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != MonitorOrder.Count)
            throw new InvalidDataException("Monitor order must contain at most ten unique device names.");
        if (Shortcuts is null || Shortcuts.Count > 128 || Shortcuts.Any(binding => binding is null))
            throw new InvalidDataException("Invalid shortcut list.");
        if (Touchpad is null) throw new InvalidDataException("Touchpad options are required.");
        var seen = new HashSet<(uint, uint)>();
        foreach (var shortcut in Shortcuts)
        {
            if (shortcut.VirtualKey is < 1 or > 254 or 0x7b || (shortcut.Modifiers & ~0x400fu) != 0 ||
                !seen.Add((shortcut.Modifiers & 0xf, shortcut.VirtualKey)))
                throw new InvalidDataException("Shortcut key/modifiers are invalid or duplicated.");
            ValidateCommand(shortcut.Command);
        }
    }

    private static void ValidateIgnores(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count > 256 ||
            values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256))
            throw new InvalidDataException("Ignore entries must be nonblank strings.");
    }

    private void ValidateCommand(ManagerCommand? command)
    {
        if (command is null || !Enum.IsDefined(command.Kind) ||
            command.Direction is { } direction && !Enum.IsDefined(direction) ||
            command.Mode is { } mode && !Enum.IsDefined(mode) ||
            command.Orientation is { } orientation && !Enum.IsDefined(orientation) ||
            command.Navigation is { } navigation && !Enum.IsDefined(navigation) ||
            command.CycleDirection is { } cycleDirection && !Enum.IsDefined(cycleDirection) ||
            command.Layout is { } layout && !Enum.IsDefined(layout) ||
            command.LayeredLayoutOptions is { IsValid: false })
            throw new InvalidDataException("Shortcut command has an unknown enum value.");
        var valid = command.Kind switch
        {
            CommandKind.Move => command.Direction is not null,
            CommandKind.MoveSpatial => command.Direction is not null,
            CommandKind.Resize => command.Direction is not null && command.Amount is > 0 and <= 0.8,
            CommandKind.FocusMonitor => command.MonitorNumber is >= 1 and <= 10,
            CommandKind.SetMode => command.Mode is not null,
            CommandKind.SetSplit => command.Orientation is not null,
            CommandKind.Pause => command.Paused is not null,
            CommandKind.ActivateWorkspace =>
                command.WorkspaceId is { } id && id != Guid.Empty || command.WorkspaceName is { } name &&
                Workspaces.Any(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase)),
            CommandKind.MoveToWorkspace =>
                command.WorkspaceId is { } id && id != Guid.Empty || command.WorkspaceName is { } name &&
                Workspaces.Any(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                command.WorkspaceNumber is { } number && number >= 1 && number <= Workspaces.Count,
            CommandKind.SetWorkspacePosition => command.Position is { IsValid: true } &&
                                                (command.WorkspaceId is { } id && id != Guid.Empty ||
                                                 command.WorkspaceName is { } name &&
                                                 Workspaces.Any(workspace => string.Equals(workspace.Name, name,
                                                     StringComparison.OrdinalIgnoreCase))),
            CommandKind.SetWorkspaceLayout => command.Layout is not null &&
                                              (command.WorkspaceId is { } id && id != Guid.Empty ||
                                               command.WorkspaceName is { } name &&
                                               Workspaces.Any(workspace => string.Equals(workspace.Name, name,
                                                   StringComparison.OrdinalIgnoreCase))),
            CommandKind.MoveToMonitor => command.MonitorId is { } id && id != Guid.Empty ||
                                         command.MonitorNumber is >= 1 and <= 10,
            CommandKind.SelectContainer => command.NodeId is null ||
                                           command.NodeId != Guid.Empty && command.Navigation is null &&
                                           command.WindowId is null,
            CommandKind.FocusCycle => command.Direction is null &&
                                      (command.WorkspaceId is null && command.WorkspaceName is null ||
                                       command.WorkspaceId is { } id && id != Guid.Empty ||
                                       command.WorkspaceName is { } name &&
                                       Workspaces.Any(workspace =>
                                           string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase))),
            _ => true
        };
        if (!valid || command.WorkspaceId is not null && command.WorkspaceName is not null ||
            command.WorkspaceNumber is not null &&
            (command.Kind != CommandKind.MoveToWorkspace || command.WorkspaceId is not null ||
             command.WorkspaceName is not null || command.WorkspaceNumber is < 1 or > 10) ||
            command.Kind is not (CommandKind.FocusMonitor or CommandKind.MoveToMonitor) &&
            command.MonitorNumber is not null ||
            command.Kind == CommandKind.MoveToMonitor &&
            (command.MonitorId is null && command.MonitorNumber is null ||
             command.MonitorId is not null && command.MonitorNumber is not null) ||
            command.Kind == CommandKind.FocusMonitor && (command.WorkspaceId is not null ||
                                                         command.WorkspaceName is not null ||
                                                         command.WorkspaceNumber is not null ||
                                                         command.WindowId is not null ||
                                                         command.MonitorId is not null ||
                                                         command.Direction is not null ||
                                                         command.Amount is not null ||
                                                         command.Orientation is not null || command.Mode is not null ||
                                                         command.Paused is not null || command.NodeId is not null ||
                                                         command.Navigation is not null ||
                                                         command.CycleDirection is not null ||
                                                         command.Layout is not null || command.Position is not null ||
                                                         command.LayeredLayoutOptions is not null) ||
            command.Kind == CommandKind.MoveSpatial && (command.WorkspaceId is not null ||
                                                        command.WorkspaceName is not null ||
                                                        command.WorkspaceNumber is not null ||
                                                        command.MonitorId is not null ||
                                                        command.MonitorNumber is not null ||
                                                        command.Amount is not null ||
                                                        command.Orientation is not null || command.Mode is not null ||
                                                        command.Paused is not null ||
                                                        command.NodeId is not null || command.Navigation is not null ||
                                                        command.CycleDirection is not null ||
                                                        command.Layout is not null || command.Position is not null ||
                                                        command.LayeredLayoutOptions is not null) ||
            command.Kind == CommandKind.ActivateWorkspace && command.MonitorId == Guid.Empty ||
            command.Kind != CommandKind.SetWorkspaceLayout && command.Layout is not null ||
            command.Kind != CommandKind.SetWorkspaceLayout && command.LayeredLayoutOptions is not null ||
            command.Kind != CommandKind.SetWorkspacePosition && command.Position is not null ||
            command.Kind == CommandKind.SetWorkspacePosition &&
            (command.WindowId is not null || command.MonitorId is not null || command.Direction is not null ||
             command.Amount is not null || command.Orientation is not null || command.Mode is not null ||
             command.Paused is not null || command.NodeId is not null || command.Navigation is not null ||
             command.CycleDirection is not null || command.Layout is not null) ||
            command.Kind == CommandKind.SetWorkspaceLayout && (command.WindowId is not null ||
                                                               command.Direction is not null ||
                                                               command.Amount is not null ||
                                                               command.Orientation is not null ||
                                                               command.Mode is not null ||
                                                               command.Paused is not null) ||
            command.MonitorId is not null &&
            command.Kind is not (CommandKind.ActivateWorkspace or CommandKind.MoveToMonitor) ||
            command.Kind != CommandKind.SelectContainer &&
            (command.NodeId is not null || command.Navigation is not null) ||
            command.Kind != CommandKind.FocusCycle && command.CycleDirection is not null)
            throw new InvalidDataException("Shortcut command arguments are invalid.");
    }

    public IReadOnlyList<ShortcutBinding> BuildShortcutBindings()
    {
        var bindings = EnableDefaultShortcuts ? CreateDefaultShortcuts() : [];
        foreach (var custom in Shortcuts)
        {
            var index = bindings.FindIndex(binding => (binding.Modifiers & 0xF) == (custom.Modifiers & 0xF) &&
                                                      binding.VirtualKey == custom.VirtualKey);
            if (index >= 0) bindings[index] = custom;
            else bindings.Add(custom);
        }

        return bindings.AsReadOnly();
    }

    private List<ShortcutBinding> CreateDefaultShortcuts()
    {
        const uint alt = 1;
        const uint control = 2;
        const uint shift = 4;
        const uint noRepeat = 0x4000;
        var bindings = new List<ShortcutBinding>();
        for (var number = 1; number <= 10; number++)
        {
            var virtualKey = number == 10 ? 0x30u : (uint)(0x30 + number);
            bindings.Add(new(alt | noRepeat, virtualKey,
                new ManagerCommand(CommandKind.FocusMonitor, MonitorNumber: number)));
            bindings.Add(new(alt | shift | noRepeat, virtualKey,
                new ManagerCommand(CommandKind.MoveToMonitor, MonitorNumber: number)));
        }

        for (var index = 0; index < 4; index++)
        {
            var direction = (Direction)index;
            var virtualKey = direction switch
            {
                Direction.Left => 0x25u,
                Direction.Up => 0x26u,
                Direction.Right => 0x27u,
                Direction.Down => 0x28u,
                _ => throw new ArgumentOutOfRangeException()
            };
            bindings.Add(new(control | alt | noRepeat, virtualKey,
                new ManagerCommand(CommandKind.Focus, Direction: direction)));
            bindings.Add(new(control | alt | shift | noRepeat, virtualKey,
                new ManagerCommand(CommandKind.MoveSpatial, Direction: direction)));
        }

        var vimBindings = new (Direction Direction, uint VirtualKey)[]
        {
            (Direction.Left, 0x48), (Direction.Down, 0x4A),
            (Direction.Up, 0x4B), (Direction.Right, 0x4C)
        };
        foreach (var (direction, virtualKey) in vimBindings)
        {
            bindings.Add(new(alt | noRepeat, virtualKey,
                new ManagerCommand(CommandKind.Focus, Direction: direction)));
            bindings.Add(new(alt | shift | noRepeat, virtualKey,
                new ManagerCommand(CommandKind.MoveSpatial, Direction: direction)));
        }

        return bindings;
    }

    public static ManagerConfiguration Load(string path, bool createDefault)
    {
        if (!File.Exists(path) && createDefault)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, Serialize(new ManagerConfiguration()));
        }

        if (new FileInfo(path).Length > Protocol.MaximumFrameBytes)
            throw new InvalidDataException("Configuration is too large.");
        return Parse(File.ReadAllText(path));
    }

    public static ManagerConfiguration Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (System.Text.Encoding.UTF8.GetByteCount(content) > Protocol.MaximumFrameBytes)
            throw new InvalidDataException("Configuration is too large.");
        if (content.StartsWith('\uFEFF')) content = content[1..];
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var typeInfo =
            (JsonTypeInfo<ManagerConfiguration>)ConfigurationJsonOptions.GetTypeInfo(typeof(ManagerConfiguration));
        var input = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ??
                    throw new JsonException("Configuration must be a JSON object.");
        var result = MergeDefaults(input).Deserialize(typeInfo) ??
                     throw new InvalidDataException("Configuration is null.");
        result.Validate();
        return result;
    }

    public static string Serialize(ManagerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        return JsonSerializer.Serialize(configuration,
            (JsonTypeInfo<ManagerConfiguration>)ConfigurationJsonOptions.GetTypeInfo(typeof(ManagerConfiguration)));
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate configuration property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray())
                RejectDuplicateProperties(value);
    }

    private static JsonObject MergeDefaults(JsonObject input)
    {
        var configurationTypeInfo = (JsonTypeInfo<ManagerConfiguration>)ConfigurationJsonOptions.GetTypeInfo(
            typeof(ManagerConfiguration));
        var defaults = JsonSerializer.SerializeToNode(new ManagerConfiguration(), configurationTypeInfo)!.AsObject();
        Overlay(defaults, input);

        if (defaults["workspaces"] is JsonArray workspaces)
        {
            var workspaceTypeInfo = Protocol.TypeInfo<WorkspaceDefinition>();
            for (var index = 0; index < workspaces.Count; index++)
            {
                if (workspaces[index] is not JsonObject workspace) continue;
                var workspaceDefaults = JsonSerializer.SerializeToNode(new WorkspaceDefinition(string.Empty),
                    workspaceTypeInfo)!.AsObject();
                workspaceDefaults.Remove("name");
                Overlay(workspaceDefaults, workspace);
                workspaces[index] = workspaceDefaults;
            }
        }

        return defaults;
    }

    private static void Overlay(JsonObject destination, JsonObject source)
    {
        foreach (var property in source)
        {
            var existingName = destination.Select(pair => pair.Key).FirstOrDefault(name =>
                string.Equals(name, property.Key, StringComparison.OrdinalIgnoreCase));
            destination[existingName ?? property.Key] = property.Value?.DeepClone();
        }
    }
}