using System.Text.Json;
using System.Text.Json.Nodes;
using WinManager.Contracts;

namespace WinManager.DebugUi;

internal static class ConfigurationEditorPresentation
{
    public static string MergeWorkspaceSettings(string content, ManagerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(snapshot);
        var workspaces = snapshot.Workspaces ??
                         throw new InvalidDataException("The manager snapshot does not contain workspace settings.");
        var root = JsonNode.Parse(content) as JsonObject ??
                   throw new InvalidDataException("Configuration must be a JSON object before workspace settings can be merged.");
        var existingWorkspaces = root["workspaces"] as JsonArray ?? [];
        var monitorHints = existingWorkspaces.OfType<JsonObject>()
            .Where(item => item["name"] is JsonValue name && name.TryGetValue<string>(out _))
            .ToDictionary(item => item["name"]!.GetValue<string>(), item => item["monitorDeviceName"]?.DeepClone(),
                StringComparer.OrdinalIgnoreCase);
        var merged = new JsonArray();
        foreach (var workspace in workspaces)
        {
            var item = new JsonObject
            {
                ["name"] = workspace.Name,
                ["layout"] = JsonSerializer.SerializeToNode(workspace.Layout, Protocol.JsonOptions),
                ["position"] = JsonSerializer.SerializeToNode(workspace.Position, Protocol.JsonOptions),
                ["layeredLayoutOptions"] = JsonSerializer.SerializeToNode(
                    workspace.LayeredLayoutOptions ?? LayeredLayoutOptions.Default, Protocol.JsonOptions)
            };
            if (monitorHints.TryGetValue(workspace.Name, out var monitorDeviceName))
                item["monitorDeviceName"] = monitorDeviceName;
            merged.Add(item);
        }

        root["workspaces"] = merged;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
