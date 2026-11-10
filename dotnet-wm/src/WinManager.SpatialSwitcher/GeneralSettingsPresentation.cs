using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinManager.SpatialSwitcher;

internal sealed record GeneralSettings(
    int InnerGap,
    int OuterGap,
    bool EnableDefaultShortcuts,
    bool TouchpadEnabled);

internal static class GeneralSettingsPresentation
{
    public static GeneralSettings Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var root = JsonNode.Parse(content) as JsonObject ??
                   throw new InvalidDataException("Configuration must be a JSON object.");
        var touchpad = GetProperty(root, "touchpad") as JsonObject;
        return new GeneralSettings(ReadValue(root, "innerGap", 8), ReadValue(root, "outerGap", 8),
            ReadValue(root, "enableDefaultShortcuts", true),
            touchpad is null || ReadValue(touchpad, "enabled", true));
    }

    public static string Update(string content, int innerGap, int outerGap, bool enableDefaultShortcuts,
        bool touchpadEnabled)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (innerGap is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(innerGap));
        if (outerGap is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(outerGap));
        var root = JsonNode.Parse(content) as JsonObject ??
                   throw new InvalidDataException("Configuration must be a JSON object.");
        SetProperty(root, "innerGap", JsonValue.Create(innerGap));
        SetProperty(root, "outerGap", JsonValue.Create(outerGap));
        SetProperty(root, "enableDefaultShortcuts", JsonValue.Create(enableDefaultShortcuts));
        var touchpad = GetProperty(root, "touchpad") as JsonObject;
        if (touchpad is null)
        {
            touchpad = new JsonObject();
            SetProperty(root, "touchpad", touchpad);
        }

        SetProperty(touchpad, "enabled", JsonValue.Create(touchpadEnabled));
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static T ReadValue<T>(JsonObject source, string name, T fallback)
    {
        var value = GetProperty(source, name);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<T>(out var result) ? result : fallback;
    }

    private static JsonNode? GetProperty(JsonObject source, string name) =>
        source.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void SetProperty(JsonObject target, string name, JsonNode? value)
    {
        var existingName = target.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        target[existingName ?? name] = value;
    }
}