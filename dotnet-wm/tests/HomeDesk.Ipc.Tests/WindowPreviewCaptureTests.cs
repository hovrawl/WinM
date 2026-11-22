using Avalonia;
using Avalonia.Controls;
using HomeDesk.Contracts;
using HomeDeskManager;
using System.Runtime.InteropServices;
using Xunit;

namespace HomeDesk.Ipc.Tests;

public sealed class WindowPreviewCaptureTests
{
    [Fact]
    public void SnapshotBackendPreservesCancellationInsteadOfReportingCaptureFailure()
    {
        var cancellation = new OperationCanceledException("Preview lookup was dismissed.");

        var normalized = WindowsSnapshotBackend.NormalizeCaptureError(cancellation, default);

        Assert.Same(cancellation, normalized);
        Assert.IsNotType<PreviewCaptureException>(normalized);
    }

    [Fact]
    public void CaptureItemInteropCallsFirstIUnknownMethod()
    {
        var requestedWindow = new nint(1234);
        var expectedInterface = Guid.NewGuid();
        var returnedItem = new IntPtr(5678);
        var invoked = false;
        var wrongSlotInvoked = false;
        WindowsSnapshotBackend.VtCreateForWindowDelegate callback =
            (IntPtr self, nint window, ref Guid interfaceId, out IntPtr item) =>
        {
            Assert.NotEqual(IntPtr.Zero, self);
            Assert.Equal(requestedWindow, window);
            Assert.Equal(expectedInterface, interfaceId);
            invoked = true;
            item = returnedItem;
            return 0;
        };
        WindowsSnapshotBackend.VtCreateForWindowDelegate wrongSlot =
            (IntPtr self, nint window, ref Guid interfaceId, out IntPtr item) =>
        {
            wrongSlotInvoked = true;
            item = IntPtr.Zero;
            return unchecked((int)0x80004005);
        };
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        var wrongSlotPointer = Marshal.GetFunctionPointerForDelegate(wrongSlot);
        using var fakeComObject = new FakeComObject(callbackPointer, 3, wrongSlotPointer, 6);

        var result = WindowsSnapshotBackend.InvokeCreateForWindow(fakeComObject.Pointer, requestedWindow,
            ref expectedInterface, out var itemPointer);

        Assert.Equal(0, result);
        Assert.True(invoked);
        Assert.False(wrongSlotInvoked);
        Assert.Equal(returnedItem, itemPointer);
    }

    [Fact]
    public void Direct3DInterfaceAccessCallsFirstIUnknownMethod()
    {
        var expectedInterface = Guid.NewGuid();
        var returnedInterface = new IntPtr(5678);
        var invoked = false;
        var wrongSlotInvoked = false;
        WindowsSnapshotBackend.VtGetInterfaceDelegate callback =
            (IntPtr self, ref Guid interfaceId, out IntPtr result) =>
        {
            Assert.NotEqual(IntPtr.Zero, self);
            Assert.Equal(expectedInterface, interfaceId);
            invoked = true;
            result = returnedInterface;
            return 0;
        };
        WindowsSnapshotBackend.VtGetInterfaceDelegate wrongSlot =
            (IntPtr self, ref Guid interfaceId, out IntPtr result) =>
        {
            wrongSlotInvoked = true;
            result = IntPtr.Zero;
            return unchecked((int)0x80004005);
        };
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        var wrongSlotPointer = Marshal.GetFunctionPointerForDelegate(wrongSlot);
        using var fakeComObject = new FakeComObject(callbackPointer, 3, wrongSlotPointer, 6);

        var resultCode = WindowsSnapshotBackend.InvokeGetInterface(fakeComObject.Pointer,
            ref expectedInterface, out var interfacePointer);

        Assert.Equal(0, resultCode);
        Assert.True(invoked);
        Assert.False(wrongSlotInvoked);
        Assert.Equal(returnedInterface, interfacePointer);
    }

    [Fact]
    public void CaptureFramePoolCreatesSessionFromDeclaredVtableSlot()
    {
        var expectedItem = new IntPtr(1234);
        var returnedSession = new IntPtr(5678);
        var invoked = false;
        var wrongSlotInvoked = false;
        WindowsSnapshotBackend.VtCreateCaptureSessionDelegate callback =
            (IntPtr self, IntPtr item, out IntPtr session) =>
            {
                Assert.NotEqual(IntPtr.Zero, self);
                Assert.Equal(expectedItem, item);
                invoked = true;
                session = returnedSession;
                return 0;
            };
        WindowsSnapshotBackend.VtCreateCaptureSessionDelegate wrongSlot =
            (IntPtr self, IntPtr item, out IntPtr session) =>
            {
                wrongSlotInvoked = true;
                session = IntPtr.Zero;
                return unchecked((int)0x80004005);
            };
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        var wrongSlotPointer = Marshal.GetFunctionPointerForDelegate(wrongSlot);
        using var fakeComObject = new FakeComObject(callbackPointer, 10, wrongSlotPointer, 6);

        var result = WindowsSnapshotBackend.InvokeCreateCaptureSession(fakeComObject.Pointer, expectedItem,
            out var sessionPointer);

        Assert.Equal(0, result);
        Assert.True(invoked);
        Assert.False(wrongSlotInvoked);
        Assert.Equal(returnedSession, sessionPointer);
    }

    [Fact]
    public void CaptureFramePoolGetsNextFrameFromDeclaredVtableSlot()
    {
        var returnedFrame = new IntPtr(5678);
        var invoked = false;
        var wrongSlotInvoked = false;
        WindowsSnapshotBackend.VtTryGetNextFrameDelegate callback = (IntPtr self, out IntPtr frame) =>
        {
            Assert.NotEqual(IntPtr.Zero, self);
            invoked = true;
            frame = returnedFrame;
            return 0;
        };
        WindowsSnapshotBackend.VtTryGetNextFrameDelegate wrongSlot = (IntPtr self, out IntPtr frame) =>
        {
            wrongSlotInvoked = true;
            frame = IntPtr.Zero;
            return unchecked((int)0x80004005);
        };
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
        var wrongSlotPointer = Marshal.GetFunctionPointerForDelegate(wrongSlot);
        using var fakeComObject = new FakeComObject(callbackPointer, 7, wrongSlotPointer, 8);

        var result = WindowsSnapshotBackend.InvokeTryGetNextFrame(fakeComObject.Pointer, out var framePointer);

        Assert.Equal(0, result);
        Assert.True(invoked);
        Assert.False(wrongSlotInvoked);
        Assert.Equal(returnedFrame, framePointer);
    }

    [Fact]
    public void OwnedFrameCopiesCapturedPixels()
    {
        var source = new byte[] { 1, 2, 3, 4 };

        var frame = new OwnedPreviewFrame(1, 1, source);
        source[0] = 9;

        Assert.Equal(1, frame.Width);
        Assert.Equal(1, frame.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame.PixelBytes.ToArray());
    }

    [Fact]
    public async Task CaptureServiceIgnoresCompletionForRemovedTarget()
    {
        var firstTarget = Target();
        var secondTarget = Target();
        var firstCaptureStarted = NewSignal();
        var firstCapture =
            new TaskCompletionSource<OwnedPreviewFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFramePublished = NewSignal();
        await using var capture = new WindowPreviewCapture((target, _) =>
        {
            if (target == firstTarget)
            {
                firstCaptureStarted.TrySetResult();
                return firstCapture.Task;
            }

            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [2, 2, 2, 2]));
        });
        capture.FrameChanged += (_, args) =>
        {
            if (args.WindowId == secondTarget.WindowId && args.Frame is not null)
                secondFramePublished.TrySetResult();
        };

        capture.SetTargets([firstTarget]);
        capture.BeginSnapshotBatch([firstTarget.WindowId]);
        await firstCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.SetTargets([secondTarget]);
        capture.BeginSnapshotBatch([secondTarget.WindowId]);
        firstCapture.SetResult(new OwnedPreviewFrame(1, 1, [1, 1, 1, 1]));
        await secondFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var frames = capture.LatestFrames;
        Assert.DoesNotContain(firstTarget.WindowId, frames.Keys);
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, frames[secondTarget.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task CaptureServiceCapturesOncePerBatchAndAgainOnReopen()
    {
        var target = Target();
        var firstFramePublished = NewSignal();
        var secondFramePublished = NewSignal();
        var captures = 0;
        await using var capture = new WindowPreviewCapture((_, _) =>
        {
            var value = Interlocked.Increment(ref captures);
            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [(byte)value, 0, 0, 255]));
        });
        capture.FrameChanged += (_, args) =>
        {
            if (args.Frame is { } firstFrame && firstFrame.PixelBytes.Span[0] == 1)
                firstFramePublished.TrySetResult();
            if (args.Frame is { } frame && frame.PixelBytes.Span[0] == 2) secondFramePublished.TrySetResult();
        };

        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await firstFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref captures));

        capture.BeginSnapshotBatch([target.WindowId]);
        await secondFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(capture.LatestFrames);
        Assert.Equal(new byte[] { 2, 0, 0, 255 }, capture.LatestFrames[target.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task CaptureRetriesTransientFailureAndPublishesTheFirstAvailableFrame()
    {
        var target = Target();
        var recoveredFramePublished = NewSignal();
        var captureCount = 0;
        await using var capture = new WindowPreviewCapture((_, _) =>
        {
            if (Interlocked.Increment(ref captureCount) == 1) return Task.FromResult<OwnedPreviewFrame?>(null);
            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [7, 8, 9, 255]));
        }, workerCount: 1);
        capture.FrameChanged += (_, args) =>
        {
            if (args.Frame?.PixelBytes.Span[0] == 7) recoveredFramePublished.TrySetResult();
        };

        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await recoveredFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref captureCount));
        Assert.Empty(capture.LatestFailures);
        Assert.Equal(new byte[] { 7, 8, 9, 255 }, capture.LatestFrames[target.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task ReopeningDuringAnInFlightFailureDoesNotConsumeTheNewBatchAttempt()
    {
        var target = Target();
        var firstCaptureStarted = NewSignal();
        var recoveredFramePublished = NewSignal();
        var releaseFirstCapture =
            new TaskCompletionSource<OwnedPreviewFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        await using var capture = new WindowPreviewCapture((_, _) =>
        {
            if (Interlocked.Increment(ref captureCount) == 1)
            {
                firstCaptureStarted.TrySetResult();
                return releaseFirstCapture.Task;
            }

            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [4, 5, 6, 255]));
        }, workerCount: 1);
        capture.FrameChanged += (_, args) =>
        {
            if (args.Frame?.PixelBytes.Span[0] == 4) recoveredFramePublished.TrySetResult();
        };

        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await firstCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.BeginSnapshotBatch([target.WindowId]);
        releaseFirstCapture.SetResult(null);
        await recoveredFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref captureCount));
        Assert.Equal(new byte[] { 4, 5, 6, 255 }, capture.LatestFrames[target.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task PausingRetainsCacheAndReopeningStartsOneNewSnapshot()
    {
        var target = Target();
        var calls = 0;
        var firstFramePublished = NewSignal();
        var secondFramePublished = NewSignal();
        await using var capture = new WindowPreviewCapture((_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [(byte)call, 0, 0, 255]));
        });
        capture.FrameChanged += (_, args) =>
        {
            if (args.Frame is not { } frame) return;
            if (frame.PixelBytes.Span[0] == 1) firstFramePublished.TrySetResult();
            if (frame.PixelBytes.Span[0] == 2) secondFramePublished.TrySetResult();
        };

        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await firstFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.Pause();
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Contains(target.WindowId, capture.LatestFrames.Keys);
        Assert.Empty(capture.LatestFailures);

        capture.SetTargets([target]);
        Assert.Contains(target.WindowId, capture.LatestFrames.Keys);
        capture.BeginSnapshotBatch([target.WindowId]);
        await secondFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 2, 0, 0, 255 }, capture.LatestFrames[target.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task MissingFramesAreScheduledInProvidedPriorityOrder()
    {
        var lowerPriority = Target();
        var middlePriority = Target();
        var highestPriority = Target();
        var started = NewSignal();
        var holdCapture =
            new TaskCompletionSource<OwnedPreviewFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureOrder = new List<Guid>();
        await using var capture = new WindowPreviewCapture((target, _) =>
        {
            captureOrder.Add(target.WindowId);
            started.TrySetResult();
            return holdCapture.Task;
        }, workerCount: 1);

        capture.SetTargets([lowerPriority, middlePriority, highestPriority]);
        capture.BeginSnapshotBatch([highestPriority.WindowId, middlePriority.WindowId, lowerPriority.WindowId]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(highestPriority.WindowId, Assert.Single(captureOrder));
        holdCapture.SetResult(new OwnedPreviewFrame(1, 1, [1, 2, 3, 4]));
    }

    [Fact]
    public async Task CaptureConcurrencyIsBoundedToThreeWorkers()
    {
        var targets = Enumerable.Range(0, 6).Select(_ => Target()).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeCapturesStarted = NewSignal();
        var active = 0;
        var maximumActive = 0;
        await using var capture = new WindowPreviewCapture(async (_, cancellationToken) =>
        {
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            if (currentActive == 3) threeCapturesStarted.TrySetResult();
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
                return new OwnedPreviewFrame(1, 1, [1, 2, 3, 4]);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        capture.SetTargets(targets);
        capture.BeginSnapshotBatch(targets.Select(target => target.WindowId));
        await threeCapturesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, Volatile.Read(ref maximumActive));

        gate.SetResult();
    }

    [Fact]
    public async Task CaptureFailureRetriesAreBoundedAndPreserveTheLastGoodFrame()
    {
        var target = Target();
        var firstFramePublished = NewSignal();
        var secondCaptureFailed = NewSignal();
        var captureCount = 0;
        await using var capture = new WindowPreviewCapture((_, _) =>
        {
            if (Interlocked.Increment(ref captureCount) == 1)
                return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [7, 8, 9, 255]));
            throw new InvalidOperationException("source is protected");
        });
        capture.FrameChanged += (_, args) =>
        {
            if (args.Frame is not null && args.Failure is null) firstFramePublished.TrySetResult();
            if (args.Failure is not null) secondCaptureFailed.TrySetResult();
        };

        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await firstFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.BeginSnapshotBatch([target.WindowId]);
        await secondCaptureFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(4, Volatile.Read(ref captureCount));
        Assert.Equal("InvalidOperationException: source is protected", capture.LatestFailures[target.WindowId]);
        Assert.Equal(new byte[] { 7, 8, 9, 255 }, capture.LatestFrames[target.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task PausingDuringRetryBackoffDefersTheNextAttemptUntilReopen()
    {
        var target = Target();
        var firstAttemptStarted = NewSignal();
        var recoveredFramePublished = NewSignal();
        var captureCount = 0;
        await using var capture = new WindowPreviewCapture((_, _) =>
        {
            var attempt = Interlocked.Increment(ref captureCount);
            if (attempt == 1)
            {
                firstAttemptStarted.TrySetResult();
                return Task.FromResult<OwnedPreviewFrame?>(null);
            }

            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [1, 2, 3, 255]));
        }, workerCount: 1);
        capture.FrameChanged += (_, args) =>
        {
            if (args.Frame is not null) recoveredFramePublished.TrySetResult();
        };

        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await firstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.BeginSnapshotBatch([]);
        await Task.Delay(250);

        Assert.Equal(1, Volatile.Read(ref captureCount));

        capture.BeginSnapshotBatch([target.WindowId]);
        await recoveredFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref captureCount));
    }

    [Fact]
    public void CaptureDiagnosticsRecordStageOutcomesAsJsonLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"preview-capture-{Guid.NewGuid():N}.jsonl");
        var windowId = Guid.NewGuid();
        try
        {
            var diagnostics = new PreviewCaptureDiagnostics(path);
            diagnostics.RecordCapture(Guid.NewGuid(), windowId, 2, "failure", 45, 1, 2, 1000, 0, 0,
                1003, "timeout", "TimeoutException", unchecked((int)0x800705B4));
            diagnostics.RecordUiHandoff(windowId, 3.5);

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"kind\":\"capture\"", lines[0]);
            Assert.Contains("\"attempt\":2", lines[0]);
            Assert.Contains("\"failureCode\":\"timeout\"", lines[0]);
            Assert.Contains("\"deviceSetupMs\":1", lines[0]);
            Assert.Contains("\"frameWaitMs\":1000", lines[0]);
            Assert.Contains("\"kind\":\"ui-handoff\"", lines[1]);
            Assert.Contains("\"uiHandoffMs\":3.5", lines[1]);
            Assert.DoesNotContain("title", string.Join(Environment.NewLine, lines), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CaptureServiceRecordsAdapterStageTimings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"preview-capture-{Guid.NewGuid():N}.jsonl");
        var target = Target();
        var framePublished = NewSignal();
        try
        {
            var diagnostics = new PreviewCaptureDiagnostics(path);
            var timings = new PreviewCaptureTimings(1.25, 2.5, 3.75, 4.5, 5.25);
            await using var capture = new WindowPreviewCapture((_, _) =>
                    Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [1, 2, 3, 4], timings)),
                diagnostics: diagnostics);
            capture.FrameChanged += (_, args) =>
            {
                if (args.Frame is not null) framePublished.TrySetResult();
            };

            capture.SetTargets([target]);
            capture.BeginSnapshotBatch([target.WindowId]);
            await framePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var record = File.ReadAllText(path);
            Assert.Contains("\"deviceSetupMs\":1.25", record);
            Assert.Contains("\"sessionSetupMs\":2.5", record);
            Assert.Contains("\"frameWaitMs\":3.75", record);
            Assert.Contains("\"readbackMs\":4.5", record);
            Assert.Contains("\"resizeMs\":5.25", record);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".1")) File.Delete(path + ".1");
        }
    }

    [Fact]
    public async Task CaptureServiceRecordsSubscriberExceptionsWithoutBlockingOtherSubscribers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"preview-capture-{Guid.NewGuid():N}.jsonl");
        var target = Target();
        var framePublished = NewSignal();
        try
        {
            var diagnostics = new PreviewCaptureDiagnostics(path);
            await using var capture = new WindowPreviewCapture((_, _) =>
                    Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [1, 2, 3, 4])),
                diagnostics: diagnostics);
            capture.FrameChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
            capture.FrameChanged += (_, args) =>
            {
                if (args.Frame is not null) framePublished.TrySetResult();
            };

            capture.SetTargets([target]);
            capture.BeginSnapshotBatch([target.WindowId]);
            await framePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var record = File.ReadAllText(path);
            Assert.Contains("\"kind\":\"frame-callback\"", record);
            Assert.Contains("\"failureCode\":\"callback-error\"", record);
            Assert.Contains("\"exceptionType\":\"System.InvalidOperationException\"", record);
            Assert.DoesNotContain("subscriber failure", record);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".1")) File.Delete(path + ".1");
        }
    }

    [Fact]
    public async Task ReplacementIdentityCannotBeOverwrittenByAnOldCapture()
    {
        var oldTarget = Target();
        var replacement = oldTarget with { HandleValue = 2 };
        var oldCaptureStarted = NewSignal();
        var releaseOldCapture =
            new TaskCompletionSource<OwnedPreviewFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementFramePublished = NewSignal();
        await using var capture = new WindowPreviewCapture((target, _) =>
        {
            if (target == oldTarget)
            {
                oldCaptureStarted.TrySetResult();
                return releaseOldCapture.Task;
            }

            return Task.FromResult<OwnedPreviewFrame?>(new OwnedPreviewFrame(1, 1, [2, 2, 2, 255]));
        });
        capture.FrameChanged += (_, args) =>
        {
            if (args.WindowId == replacement.WindowId && args.Frame?.PixelBytes.Span[0] == 2)
                replacementFramePublished.TrySetResult();
        };

        capture.SetTargets([oldTarget]);
        capture.BeginSnapshotBatch([oldTarget.WindowId]);
        await oldCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        capture.SetTargets([replacement]);
        capture.BeginSnapshotBatch([replacement.WindowId]);
        releaseOldCapture.SetResult(new OwnedPreviewFrame(1, 1, [1, 1, 1, 255]));
        await replacementFramePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 2, 2, 2, 255 }, capture.LatestFrames[replacement.WindowId].PixelBytes.ToArray());
    }

    [Fact]
    public async Task DisposingCaptureCancelsAnInFlightReadback()
    {
        var target = Target();
        var captureStarted = NewSignal();
        var captureCanceled = NewSignal();
        var capture = new WindowPreviewCapture(async (_, cancellationToken) =>
        {
            captureStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                captureCanceled.TrySetResult();
                throw;
            }

            return null;
        });
        capture.SetTargets([target]);
        capture.BeginSnapshotBatch([target.WindowId]);
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await capture.DisposeAsync();

        await captureCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PreviewSurfaceUsesRoundedClipping()
    {
        var surface = CarouselWindow.CreatePreviewSurface(new TextBlock());

        Assert.Equal(new CornerRadius(10), surface.CornerRadius);
        Assert.True(surface.ClipToBounds);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (current >= value || Interlocked.CompareExchange(ref maximum, value, current) == current) return;
        }
    }

    private static WindowPreviewTarget Target() => new(Guid.NewGuid(), 1, 1, 1);

    private sealed class FakeComObject : IDisposable
    {
        private readonly IntPtr vtable;

        public FakeComObject(IntPtr method, int methodSlot, IntPtr wrongMethod, int wrongMethodSlot)
        {
            vtable = Marshal.AllocHGlobal(11 * IntPtr.Size);
            for (var index = 0; index < 11; index++) Marshal.WriteIntPtr(vtable, index * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(vtable, methodSlot * IntPtr.Size, method);
            Marshal.WriteIntPtr(vtable, wrongMethodSlot * IntPtr.Size, wrongMethod);
            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, vtable);
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(vtable);
        }
    }
}