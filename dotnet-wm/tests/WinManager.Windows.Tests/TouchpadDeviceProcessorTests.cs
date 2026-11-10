using WinManager.Windows;
using Xunit;

namespace WinManager.Windows.Tests;

public sealed class TouchpadDeviceProcessorTests
{
    [Fact]
    public void InterleavedTouchpadsKeepIndependentSwipeRecognitionState()
    {
        var firstDevice = new TouchpadDeviceProcessor(42);
        var secondDevice = new TouchpadDeviceProcessor(84);

        Process(firstDevice, 42, 100, 1, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(secondDevice, 84, 100, 1, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(firstDevice, 42, 140, 2, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(secondDevice, 84, 140, 2, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(firstDevice, 42, 156, 3, 3, Contact(1, 0.1), Contact(2, 0.3), Contact(3, 0.5));
        Process(secondDevice, 84, 156, 3, 3, Contact(1, 0.5), Contact(2, 0.7), Contact(3, 0.9));

        var firstSwipe = Process(firstDevice, 42, 172, 4, 0);
        var secondSwipe = Process(secondDevice, 84, 172, 4, 0);

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(firstSwipe.Gestures).Direction);
        Assert.Equal(TouchpadSwipeDirection.Right, Assert.Single(secondSwipe.Gestures).Direction);
    }

    private static TouchpadDeviceProcessingResult Process(TouchpadDeviceProcessor processor, nint deviceHandle,
        long timestamp, uint scanTime, int expectedContacts, params TouchpadContact[] contacts) =>
        processor.Process(new TouchpadContactReport(deviceHandle, timestamp, scanTime, expectedContacts, contacts));

    private static TouchpadContact Contact(uint id, double x) => new(id, true, x, 0.5);
}
