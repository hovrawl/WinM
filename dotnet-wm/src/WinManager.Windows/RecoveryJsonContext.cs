using System.Text.Json.Serialization;

namespace WinManager.Windows;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RecoveryCommand))]
[JsonSerializable(typeof(RecoveryJournal))]
internal partial class RecoveryJsonContext : JsonSerializerContext
{
}
