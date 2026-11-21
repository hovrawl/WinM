using System.Text;
using System.Text.Json;

namespace HomeDeskManager;

internal sealed class PreviewCaptureDiagnostics
{
    private const long MaximumRecordingBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly string? path;

    public static PreviewCaptureDiagnostics Default { get; } = new(GetDefaultPath());

    internal PreviewCaptureDiagnostics(string? path)
    {
        this.path = path;
    }

    public string? RecordingPath => path;

    public void RecordCapture(Guid batchId, Guid windowId, int attempt, string outcome, double queueMs,
        double deviceSetupMs, double sessionSetupMs, double frameWaitMs, double readbackMs, double resizeMs,
        double totalMs, string? failureCode = null, string? exceptionType = null, int? hResult = null)
    {
        Write(new PreviewCaptureRecord(DateTimeOffset.UtcNow, "capture", batchId, windowId, attempt, outcome,
            queueMs, deviceSetupMs, sessionSetupMs, frameWaitMs, readbackMs, resizeMs, totalMs, null,
            failureCode, exceptionType, hResult));
    }

    public void RecordUiHandoff(Guid windowId, double elapsedMs)
    {
        Write(new PreviewCaptureRecord(DateTimeOffset.UtcNow, "ui-handoff", null, windowId, null, "presented",
            null, null, null, null, null, null, null, elapsedMs, null, null, null));
    }

    public void RecordFrameCallbackFailure(Guid windowId, Exception error)
    {
        Write(new PreviewCaptureRecord(DateTimeOffset.UtcNow, "frame-callback", null, windowId, null, "failure",
            null, null, null, null, null, null, null, null, "callback-error", error.GetType().FullName,
            error.HResult));
    }

    private void Write(PreviewCaptureRecord record)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var json = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
            lock (sync)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path) &&
                    new FileInfo(path).Length + Encoding.UTF8.GetByteCount(json) > MaximumRecordingBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, json, Encoding.UTF8);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Trace.WriteLine($"Preview capture recording failed: {error.GetType().Name}.");
        }
    }

    private static string? GetDefaultPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("HOMEDESK_PREVIEW_CAPTURE_LOG");
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localApplicationData)
            ? null
            : Path.Combine(localApplicationData, "HomeDeskManager", "preview-capture.jsonl");
    }

    private sealed record PreviewCaptureRecord(
        DateTimeOffset TimestampUtc,
        string Kind,
        Guid? BatchId,
        Guid WindowId,
        int? Attempt,
        string Outcome,
        double? QueueMs,
        double? DeviceSetupMs,
        double? SessionSetupMs,
        double? FrameWaitMs,
        double? ReadbackMs,
        double? ResizeMs,
        double? TotalMs,
        double? UiHandoffMs,
        string? FailureCode,
        string? ExceptionType,
        int? HResult);
}