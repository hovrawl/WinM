using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinManager.Contracts;

public sealed record RequestEnvelope(int ProtocolVersion, Guid RequestId, string Operation, JsonElement Payload,
    string Kind = "request");
public sealed record ResponseEnvelope(int ProtocolVersion, Guid RequestId, bool Success, string? ErrorCode,
    string? Message, long StateRevision, JsonElement? Payload = null, string Kind = "response");
public sealed record EventEnvelope(int ProtocolVersion, Guid InstanceId, long Sequence, long StateRevision,
    string EventType, JsonElement Payload, string Kind = "event");
public sealed record SubscribeRequest(IReadOnlyList<string>? EventTypes = null);
public sealed record UnsubscribeRequest(Guid SubscriptionId);
public sealed record SubscriptionResult(bool Success, Guid? SubscriptionId, string? ErrorCode, long StateRevision);

public static class Protocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reflection fallback is guarded by JsonSerializer.IsReflectionEnabledByDefault and removed when reflection is disabled.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reflection fallback is guarded by JsonSerializer.IsReflectionEnabledByDefault and removed in NativeAOT applications.")]
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 64,
            TypeInfoResolver = JsonSerializer.IsReflectionEnabledByDefault
                ? JsonTypeInfoResolver.Combine(ProtocolJsonContext.Default, new DefaultJsonTypeInfoResolver())
                : ProtocolJsonContext.Default
        };
        options.Converters.Add(new JsonStringEnumConverter<CommandKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<Direction>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<SplitOrientation>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ContainerNavigation>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<FocusCycleDirection>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<WindowMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)JsonOptions.GetTypeInfo(typeof(T));
    public static JsonElement EmptyPayload { get; } = CreateEmptyPayload();
    private static JsonElement CreateEmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
    public static JsonElement Payload<T>(T value) => JsonSerializer.SerializeToElement(value,
        JsonOptions.GetTypeInfo(typeof(T) == typeof(object) && value is not null ? value.GetType() : typeof(T)));
    public static string DefaultPipeName()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows user-session IPC is required.");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current user has no SID.");
        using var process = Process.GetCurrentProcess();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24];
        return $"WinManager.v{Version}.{hash}.{process.SessionId}";
    }
}

public static class PipeFraming
{
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value,
            Protocol.JsonOptions.GetTypeInfo(typeof(T) == typeof(object) && value is not null ? value.GetType() : typeof(T)));
        if (bytes.Length == 0 || bytes.Length > Protocol.MaximumFrameBytes)
            throw new InvalidDataException("Frame exceeds the protocol limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > Protocol.MaximumFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }
}