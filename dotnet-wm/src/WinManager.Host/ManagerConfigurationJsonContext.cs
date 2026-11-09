using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinManager.Host;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ManagerConfiguration))]
internal partial class ManagerConfigurationJsonContext : JsonSerializerContext
{
}
