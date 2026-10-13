using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinManager.Contracts;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(UnsubscribeRequest))]
[JsonSerializable(typeof(SubscriptionResult))]
[JsonSerializable(typeof(ManagerCommand))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(ManagerSnapshot))]
[JsonSerializable(typeof(JsonElement))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
}