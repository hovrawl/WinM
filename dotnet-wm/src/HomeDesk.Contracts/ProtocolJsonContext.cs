using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeDesk.Contracts;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(UnsubscribeRequest))]
[JsonSerializable(typeof(SubscriptionResult))]
[JsonSerializable(typeof(ConfigurationDocument))]
[JsonSerializable(typeof(SaveConfigurationRequest))]
[JsonSerializable(typeof(ManagerCommand))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(ManagerSnapshot))]
[JsonSerializable(typeof(PreviewTargetsRequest))]
[JsonSerializable(typeof(WindowPreviewTarget))]
[JsonSerializable(typeof(PreviewTargetFailure))]
[JsonSerializable(typeof(PreviewTargetsResult))]
[JsonSerializable(typeof(TouchpadOptions))]
[JsonSerializable(typeof(WorkspaceDefinition))]
[JsonSerializable(typeof(WorkspaceLayout))]
[JsonSerializable(typeof(DeskPosition))]
[JsonSerializable(typeof(LayeredLayoutOptions))]
[JsonSerializable(typeof(IReadOnlyDictionary<Guid, SplitNodeInfo?>))]
[JsonSerializable(typeof(JsonElement))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
}