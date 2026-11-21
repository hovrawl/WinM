using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeDesk.Contracts;

namespace HomeDeskManager;

internal readonly record struct PreviewCaptureTimings(
    double DeviceSetupMs,
    double SessionSetupMs,
    double FrameWaitMs,
    double ReadbackMs,
    double ResizeMs);

internal sealed class PreviewCaptureException(
    string failureCode,
    PreviewCaptureTimings timings,
    Exception? innerException = null) : Exception(
    innerException?.Message ?? $"Preview capture failed ({failureCode}).", innerException)
{
    public string FailureCode { get; } = failureCode;
    public PreviewCaptureTimings Timings { get; } = timings;
}

internal sealed class OwnedPreviewFrame(
    int width,
    int height,
    byte[] pixelBytes,
    PreviewCaptureTimings captureTimings = default)
{
    private readonly byte[] pixelBytes = pixelBytes.ToArray();

    public int Width { get; } = width;
    public int Height { get; } = height;
    public ReadOnlyMemory<byte> PixelBytes => pixelBytes;
    public int ByteLength => pixelBytes.Length;
    public PreviewCaptureTimings CaptureTimings { get; } = captureTimings;
}

internal sealed class PreviewFrameChangedEventArgs(Guid windowId, OwnedPreviewFrame? frame, string? failure) : EventArgs
{
    public Guid WindowId { get; } = windowId;
    public OwnedPreviewFrame? Frame { get; } = frame;
    public string? Failure { get; } = failure;
}

internal sealed class WindowPreviewCapture : IDisposable, IAsyncDisposable
{
    private const int DefaultWorkerCount = 3;
    private const int MaximumAttemptsPerBatch = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(500)];
    private const long MaximumCachedBytes = 128L * 1024 * 1024;
    private readonly object sync = new();
    private readonly Func<WindowPreviewTarget, CancellationToken, Task<OwnedPreviewFrame?>> captureFrame;
    private readonly WindowsSnapshotBackend? nativeBackend;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim workAvailable = new(0, 1);
    private readonly Dictionary<Guid, WindowPreviewTarget> targets = [];
    private readonly List<Guid> batchOrder = [];
    private readonly Dictionary<Guid, OwnedPreviewFrame> latestFrames = [];
    private readonly Dictionary<Guid, string> failures = [];
    private readonly Dictionary<Guid, long> lastFrameUse = [];
    private readonly Dictionary<Guid, CaptureJob> inFlight = [];
    private readonly HashSet<Guid> attempted = [];
    private readonly Dictionary<Guid, int> attemptCounts = [];
    private readonly Dictionary<Guid, DateTimeOffset> retryAfter = [];
    private readonly Task[] workers;
    private readonly PreviewCaptureDiagnostics diagnostics;
    private Guid batchId;
    private long batchStartedTimestamp;
    private long cachedBytes;
    private long frameSequence;
    private Task? disposalTask;
    private bool batchActive;
    private bool disposed;

    public WindowPreviewCapture() : this(new WindowsSnapshotBackend())
    {
    }

    private WindowPreviewCapture(WindowsSnapshotBackend backend) : this(backend.CaptureAsync)
    {
        nativeBackend = backend;
    }

    internal WindowPreviewCapture(Func<WindowPreviewTarget, CancellationToken, Task<OwnedPreviewFrame?>> captureFrame,
        int workerCount = DefaultWorkerCount, PreviewCaptureDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(captureFrame);
        if (workerCount is < 1 or > DefaultWorkerCount)
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        this.captureFrame = captureFrame;
        this.diagnostics = diagnostics ?? PreviewCaptureDiagnostics.Default;
        this.workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => CaptureWorkerAsync(shutdown.Token))).ToArray();
    }

    public event EventHandler<PreviewFrameChangedEventArgs>? FrameChanged;

    public void SetTargets(IEnumerable<WindowPreviewTarget> nextTargets)
    {
        ArgumentNullException.ThrowIfNull(nextTargets);
        var next = nextTargets.Where(IsValidTargetShape).DistinctBy(target => target.WindowId)
            .ToDictionary(target => target.WindowId);
        List<Guid> clearedFrames;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (targets.Count == next.Count && targets.All(pair => next.TryGetValue(pair.Key, out var value) &&
                                                                   value == pair.Value)) return;

            clearedFrames = latestFrames.Keys.Where(id => !next.TryGetValue(id, out var target) ||
                                                          targets[id] != target).ToList();
            foreach (var id in clearedFrames) RemoveFrame(id);
            foreach (var id in targets.Keys.Where(id => !next.ContainsKey(id) || targets[id] != next[id]).ToArray())
            {
                failures.Remove(id);
                attempted.Remove(id);
                attemptCounts.Remove(id);
                retryAfter.Remove(id);
                batchOrder.Remove(id);
            }

            targets.Clear();
            foreach (var pair in next) targets.Add(pair.Key, pair.Value);
            batchOrder.RemoveAll(id => !targets.ContainsKey(id));
        }

        foreach (var windowId in clearedFrames) RaiseFrameChanged(windowId, null, null);
        SignalWorkAvailable();
    }

    public void BeginSnapshotBatch(IEnumerable<Guid> orderedWindowIds)
    {
        ArgumentNullException.ThrowIfNull(orderedWindowIds);
        var ordered = orderedWindowIds.Distinct().ToArray();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            batchActive = true;
            batchId = Guid.NewGuid();
            batchStartedTimestamp = Stopwatch.GetTimestamp();
            attempted.Clear();
            attemptCounts.Clear();
            retryAfter.Clear();
            foreach (var (windowId, job) in inFlight)
            {
                if (targets.TryGetValue(windowId, out var currentTarget) && currentTarget == job.Target)
                    attempted.Add(windowId);
            }

            batchOrder.Clear();
            batchOrder.AddRange(ordered.Where(targets.ContainsKey));
            foreach (var windowId in batchOrder) failures.Remove(windowId);
        }

        SignalWorkAvailable();
    }

    public void UpdateSnapshotOrder(IEnumerable<Guid> orderedWindowIds)
    {
        ArgumentNullException.ThrowIfNull(orderedWindowIds);
        var ordered = orderedWindowIds.Distinct().ToArray();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!batchActive) return;
            batchOrder.Clear();
            batchOrder.AddRange(ordered.Where(targets.ContainsKey));
        }

        SignalWorkAvailable();
    }

    public void Pause()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            batchActive = false;
            batchOrder.Clear();
        }

        SignalWorkAvailable();
    }

    public IReadOnlyDictionary<Guid, OwnedPreviewFrame> LatestFrames
    {
        get
        {
            lock (sync)
            {
                foreach (var windowId in latestFrames.Keys) lastFrameUse[windowId] = ++frameSequence;
                return new Dictionary<Guid, OwnedPreviewFrame>(latestFrames);
            }
        }
    }

    public IReadOnlyDictionary<Guid, string> LatestFailures
    {
        get
        {
            lock (sync) return new Dictionary<Guid, string>(failures);
        }
    }

    public void Dispose() => _ = DisposeAsync();

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposalTask is not null) return new ValueTask(disposalTask);
            disposed = true;
            shutdown.Cancel();
            disposalTask = DisposeWorkerAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeWorkerAsync()
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                nativeBackend?.Dispose();
            }
            finally
            {
                lock (sync)
                {
                    targets.Clear();
                    batchOrder.Clear();
                    latestFrames.Clear();
                    failures.Clear();
                    attempted.Clear();
                    FrameChanged = null;
                }

                shutdown.Dispose();
                workAvailable.Dispose();
            }
        }
    }

    private async Task CaptureWorkerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = TakeNextTarget();
            if (job is null)
            {
                await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            SignalWorkAvailable();
            var target = job.Value.Target;
            var timer = Stopwatch.StartNew();
            OwnedPreviewFrame? frame = null;
            var timings = default(PreviewCaptureTimings);
            string? failure = null;
            string? failureCode = null;
            string? exceptionType = null;
            int? hResult = null;
            try
            {
                frame = await captureFrame(target, cancellationToken).ConfigureAwait(false);
                if (frame is not null) timings = frame.CaptureTimings;
                if (frame is null)
                {
                    failure = "The source did not return a frame.";
                    failureCode = "no-frame";
                }
            }
            catch (PreviewCaptureException error) when (cancellationToken.IsCancellationRequested &&
                                                        error.FailureCode == "cancelled")
            {
                timer.Stop();
                diagnostics.RecordCapture(job.Value.BatchId, target.WindowId, job.Value.Attempt, "cancelled",
                    QueueTimeMs(job.Value), error.Timings.DeviceSetupMs, error.Timings.SessionSetupMs,
                    error.Timings.FrameWaitMs, error.Timings.ReadbackMs, error.Timings.ResizeMs,
                    timer.Elapsed.TotalMilliseconds, error.FailureCode, error.InnerException?.GetType().FullName,
                    error.InnerException?.HResult);
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                timer.Stop();
                diagnostics.RecordCapture(job.Value.BatchId, target.WindowId, job.Value.Attempt, "cancelled",
                    QueueTimeMs(job.Value), 0, 0, 0, 0, 0, timer.Elapsed.TotalMilliseconds, "cancelled");
                throw;
            }
            catch (PreviewCaptureException error)
            {
                timings = error.Timings;
                failure = error.Message;
                failureCode = error.FailureCode;
                exceptionType = error.InnerException?.GetType().FullName;
                hResult = error.InnerException?.HResult;
            }
            catch (Exception error)
            {
                failure = $"{error.GetType().Name}: {error.Message}";
                failureCode = "capture-error";
                exceptionType = error.GetType().FullName;
                hResult = error.HResult;
            }

            timer.Stop();
            diagnostics.RecordCapture(job.Value.BatchId, target.WindowId, job.Value.Attempt,
                frame is not null ? "success" : "failure", QueueTimeMs(job.Value), timings.DeviceSetupMs,
                timings.SessionSetupMs, timings.FrameWaitMs, timings.ReadbackMs, timings.ResizeMs,
                timer.Elapsed.TotalMilliseconds, failureCode, exceptionType, hResult);
            CompleteCapture(job.Value, frame, failure);
        }
    }

    private CaptureJob? TakeNextTarget()
    {
        lock (sync)
        {
            if (disposed || !batchActive || batchOrder.Count == 0) return null;
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var id in batchOrder)
                {
                    if (inFlight.ContainsKey(id) || !targets.TryGetValue(id, out var target))
                        continue;
                    var hasAttempted = attempted.Contains(id);
                    var hasFrame = latestFrames.ContainsKey(id);
                    if (pass == 0 && (hasAttempted || hasFrame) || pass == 1 && (hasAttempted || !hasFrame))
                        continue;
                    if (pass == 2 && (!hasAttempted || !retryAfter.TryGetValue(id, out var scheduledAt) ||
                                      scheduledAt > DateTimeOffset.UtcNow ||
                                      attemptCounts.GetValueOrDefault(id) >= MaximumAttemptsPerBatch)) continue;

                    var attempt = attemptCounts.GetValueOrDefault(id) + 1;
                    attemptCounts[id] = attempt;
                    attempted.Add(id);
                    retryAfter.Remove(id);
                    var job = new CaptureJob(target, batchId, attempt, batchStartedTimestamp);
                    inFlight.Add(id, job);
                    return job;
                }
            }

            return null;
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        TimeSpan? wait = null;
        lock (sync)
        {
            if (disposed) return;
            if (batchActive)
            {
                var nextRetry = batchOrder.Where(id => !inFlight.ContainsKey(id) && retryAfter.ContainsKey(id))
                    .Select(id => retryAfter[id]).DefaultIfEmpty().Min();
                if (nextRetry != default)
                    wait = nextRetry - DateTimeOffset.UtcNow;
            }
        }

        if (wait is null)
        {
            await workAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        await workAvailable.WaitAsync(wait.Value, cancellationToken).ConfigureAwait(false);
    }

    private double QueueTimeMs(CaptureJob job) =>
        Stopwatch.GetElapsedTime(job.BatchStartedTimestamp).TotalMilliseconds;

    private void CompleteCapture(CaptureJob job, OwnedPreviewFrame? frame, string? failure)
    {
        var target = job.Target;
        var evictedFrames = new List<Guid>();
        OwnedPreviewFrame? publishedFrame;
        string? publishedFailure;
        bool targetIsCurrent;
        lock (sync)
        {
            if (inFlight.TryGetValue(target.WindowId, out var activeJob) && activeJob == job)
                inFlight.Remove(target.WindowId);
            targetIsCurrent = !disposed && targets.TryGetValue(target.WindowId, out var current) && current == target;
            if (targetIsCurrent && frame is not null)
            {
                failures.Remove(target.WindowId);
                if (batchId == job.BatchId) retryAfter.Remove(target.WindowId);
                else if (batchActive && batchOrder.Contains(target.WindowId)) attempted.Add(target.WindowId);
                if (StoreFrame(target.WindowId, frame, evictedFrames))
                    lastFrameUse[target.WindowId] = ++frameSequence;
            }
            else if (targetIsCurrent && batchActive && batchId == job.BatchId)
            {
                failure ??= "The source did not return a frame.";
                if (job.Attempt < MaximumAttemptsPerBatch)
                {
                    retryAfter[target.WindowId] = DateTimeOffset.UtcNow + RetryDelays[job.Attempt - 1];
                }
                else
                {
                    failures[target.WindowId] = failure;
                }
            }
            else if (targetIsCurrent && batchActive && batchId != job.BatchId)
            {
                attempted.Remove(target.WindowId);
                attemptCounts.Remove(target.WindowId);
                retryAfter.Remove(target.WindowId);
            }

            publishedFrame = targetIsCurrent && latestFrames.TryGetValue(target.WindowId, out var latestFrame)
                ? latestFrame
                : null;
            publishedFailure = targetIsCurrent && failures.TryGetValue(target.WindowId, out var currentFailure)
                ? currentFailure
                : null;
        }

        if (!targetIsCurrent)
        {
            SignalWorkAvailable();
            return;
        }

        RaiseFrameChanged(target.WindowId, publishedFrame, publishedFailure);
        foreach (var windowId in evictedFrames) RaiseFrameChanged(windowId, null, null);
        SignalWorkAvailable();
    }

    private readonly record struct CaptureJob(
        WindowPreviewTarget Target,
        Guid BatchId,
        int Attempt,
        long BatchStartedTimestamp);

    private bool StoreFrame(Guid windowId, OwnedPreviewFrame frame, ICollection<Guid> evictedFrames)
    {
        if (frame.ByteLength > MaximumCachedBytes) return false;
        RemoveFrame(windowId);
        latestFrames[windowId] = frame;
        cachedBytes += frame.ByteLength;
        while (cachedBytes > MaximumCachedBytes)
        {
            var leastUsed = latestFrames.Keys.Where(id => id != windowId)
                .OrderBy(id => batchOrder.Contains(id))
                .ThenBy(id => lastFrameUse.GetValueOrDefault(id)).FirstOrDefault();
            if (leastUsed == Guid.Empty) break;
            RemoveFrame(leastUsed);
            evictedFrames.Add(leastUsed);
        }

        return true;
    }

    private void RemoveFrame(Guid windowId)
    {
        if (latestFrames.Remove(windowId, out var frame)) cachedBytes -= frame.ByteLength;
        lastFrameUse.Remove(windowId);
    }

    private void RaiseFrameChanged(Guid windowId, OwnedPreviewFrame? frame, string? failure)
    {
        var handlers = FrameChanged;
        if (handlers is null) return;

        var args = new PreviewFrameChangedEventArgs(windowId, frame, failure);
        foreach (EventHandler<PreviewFrameChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception error)
            {
                diagnostics.RecordFrameCallbackFailure(windowId, error);
            }
        }
    }

    private void SignalWorkAvailable()
    {
        try
        {
            workAvailable.Release();
        }
        catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    private static bool IsValidTargetShape(WindowPreviewTarget target) =>
        target.WindowId != Guid.Empty && target.HandleValue != 0 && target.ProcessId != 0 &&
        target.ProcessCreationTime != 0;
}