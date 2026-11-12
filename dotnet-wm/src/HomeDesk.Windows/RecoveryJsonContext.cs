using System.Text.Json.Serialization;

namespace HomeDesk.Windows;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RecoveryCommand))]
[JsonSerializable(typeof(RecoveryJournal))]
internal partial class RecoveryJsonContext : JsonSerializerContext
{
}
