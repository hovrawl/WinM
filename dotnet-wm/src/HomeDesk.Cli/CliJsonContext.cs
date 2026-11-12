using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeDesk.Cli;

internal sealed record CliError(string ErrorCode, string Message);
internal sealed record StartupRegistrationResult(bool Success, string RegistrationName, string CommandLine);
internal sealed record StartupRemovalResult(bool Success, string RegistrationName);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CliError))]
[JsonSerializable(typeof(StartupRegistrationResult))]
[JsonSerializable(typeof(StartupRemovalResult))]
internal partial class CliJsonContext : JsonSerializerContext
{
}
