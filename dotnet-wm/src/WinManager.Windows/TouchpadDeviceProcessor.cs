using WinManager.Contracts;

namespace WinManager.Windows;

internal readonly record struct TouchpadDeviceProcessingResult(
    TouchpadContactFrame? Frame,
    IReadOnlyList<TouchpadGesture> Gestures,
    bool DiscardedIncompleteFrame,
    bool Malformed);

internal sealed class TouchpadDeviceProcessor
{
    private readonly nint deviceHandle;
    private readonly TouchpadFrameAssembler assembler = new();
    private readonly TouchpadGestureRecognizer recognizer;

    public TouchpadDeviceProcessor(nint deviceHandle, TouchpadOptions? options = null)
    {
        if (deviceHandle == 0) throw new ArgumentOutOfRangeException(nameof(deviceHandle));
        this.deviceHandle = deviceHandle;
        recognizer = new TouchpadGestureRecognizer(options);
    }

    public TouchpadDeviceProcessingResult Process(TouchpadContactReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.DeviceHandle != deviceHandle)
        {
            Cancel();
            return new(null, [], true, true);
        }

        var assembled = assembler.Process(report);
        if (assembled.Malformed || assembled.DiscardedIncompleteFrame) recognizer.Cancel();
        if (assembled.Frame is not { } frame)
            return new(null, [], assembled.DiscardedIncompleteFrame, assembled.Malformed);

        return new(frame, recognizer.Process(frame), assembled.DiscardedIncompleteFrame, assembled.Malformed);
    }

    public void Cancel()
    {
        assembler.Cancel();
        recognizer.Cancel();
    }
}
