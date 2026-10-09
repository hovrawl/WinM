using System.Text.Json;
using System.Text.Json.Serialization;
using WinManager.Contracts;

namespace WinManager.Host;

public sealed record ManagerConfiguration
{
    public IReadOnlyList<WorkspaceDefinition> Workspaces { get; init; } = [new("1"), new("2"), new("3")];
    public int InnerGap { get; init; } = 8;
    public int OuterGap { get; init; } = 8;
    public IReadOnlyList<string> IgnoredProcessNames { get; init; } = [];
    public IReadOnlyList<string> IgnoredClassNames { get; init; } = [];
    public IReadOnlyList<ShortcutBinding> Shortcuts { get; init; } = [];
    public int InputCapacity { get; init; } = 256;
    public int MaximumClients { get; init; } = 16;
    public int OutputCapacity { get; init; } = 128;
    public int LogMaximumBytes { get; init; } = 2 * 1024 * 1024;
    public int LogFileCount { get; init; } = 4;

    public void Validate()
    {
        if (InnerGap is < 0 or > 256 || OuterGap is < 0 or > 256) throw new InvalidDataException("Gaps must be between 0 and 256 DIPs.");
        if (InputCapacity is < 1 or > 4096 || MaximumClients is < 1 or > 64 || OutputCapacity is < 8 or > 1024)
            throw new InvalidDataException("Invalid queue or client limits.");
        if (LogMaximumBytes is < 65536 or > 20971520 || LogFileCount is < 2 or > 10)
            throw new InvalidDataException("Invalid log rotation limits.");
        if (Workspaces is null || Workspaces.Count is < 1 or > 128 ||
            Workspaces.Any(workspace => workspace is null || string.IsNullOrWhiteSpace(workspace.Name) || workspace.Name.Length > 64 ||
                workspace.MonitorDeviceName is { Length: > 256 }) ||
            Workspaces.Select(workspace => workspace.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Workspaces.Count)
            throw new InvalidDataException("Workspace names must be nonblank and unique.");
        ValidateIgnores(IgnoredProcessNames);
        ValidateIgnores(IgnoredClassNames);
        if (Shortcuts is null || Shortcuts.Count > 128 || Shortcuts.Any(binding => binding is null))
            throw new InvalidDataException("Invalid shortcut list.");
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
        if (values is null || values.Count > 256 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256))
            throw new InvalidDataException("Ignore entries must be nonblank strings.");
    }

    private void ValidateCommand(ManagerCommand? command)
    {
        if (command is null || !Enum.IsDefined(command.Kind) ||
            command.Direction is { } direction && !Enum.IsDefined(direction) ||
            command.Mode is { } mode && !Enum.IsDefined(mode) || command.Orientation is { } orientation && !Enum.IsDefined(orientation) ||
            command.Navigation is { } navigation && !Enum.IsDefined(navigation) ||
            command.CycleDirection is { } cycleDirection && !Enum.IsDefined(cycleDirection))
            throw new InvalidDataException("Shortcut command has an unknown enum value.");
        var valid = command.Kind switch
        {
            CommandKind.Move => command.Direction is not null,
            CommandKind.Resize => command.Direction is not null && command.Amount is > 0 and <= 0.8,
            CommandKind.SetMode => command.Mode is not null,
            CommandKind.SetSplit => command.Orientation is not null,
            CommandKind.Pause => command.Paused is not null,
            CommandKind.ActivateWorkspace or CommandKind.MoveToWorkspace =>
                command.WorkspaceId is { } id && id != Guid.Empty || command.WorkspaceName is { } name &&
                Workspaces.Any(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase)),
            CommandKind.MoveToMonitor => command.MonitorId is { } id && id != Guid.Empty,
            CommandKind.SelectContainer => command.NodeId is null ||
                command.NodeId != Guid.Empty && command.Navigation is null && command.WindowId is null,
            CommandKind.FocusCycle => command.Direction is null &&
                (command.WorkspaceId is null && command.WorkspaceName is null ||
                 command.WorkspaceId is { } id && id != Guid.Empty || command.WorkspaceName is { } name &&
                 Workspaces.Any(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase))),
            _ => true
        };
        if (!valid || command.WorkspaceId is not null && command.WorkspaceName is not null ||
            command.Kind != CommandKind.SelectContainer && (command.NodeId is not null || command.Navigation is not null) ||
            command.Kind != CommandKind.FocusCycle && command.CycleDirection is not null)
            throw new InvalidDataException("Shortcut command arguments are invalid.");
    }

    public static ManagerConfiguration Load(string path, bool createDefault)
    {
        var options = new JsonSerializerOptions(Protocol.JsonOptions)
        {
            WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        if (!File.Exists(path) && createDefault)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ManagerConfiguration(), options));
        }
        if (new FileInfo(path).Length > Protocol.MaximumFrameBytes) throw new InvalidDataException("Configuration is too large.");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var result = document.RootElement.Deserialize<ManagerConfiguration>(options) ?? throw new InvalidDataException("Configuration is null.");
        result.Validate();
        return result;
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
            foreach (var value in element.EnumerateArray()) RejectDuplicateProperties(value);
    }
}