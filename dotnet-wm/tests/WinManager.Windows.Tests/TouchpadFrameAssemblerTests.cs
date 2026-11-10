using WinManager.Windows;
using Xunit;

namespace WinManager.Windows.Tests;

public sealed class TouchpadFrameAssemblerTests
{
    [Fact]
    public void ReportsInOneScanAreAssembledBeforePublishingAFrame()
    {
        var assembler = new TouchpadFrameAssembler();
        var first = assembler.Process(Report(100, 7, 3, Contact(1, 0.1)));
        var completed = assembler.Process(Report(100, 7, 3, Contact(2, 0.5), Contact(3, 0.9)));

        Assert.Null(first.Frame);
        Assert.False(first.Malformed);
        Assert.False(first.DiscardedIncompleteFrame);
        Assert.Equal(3, Assert.IsType<TouchpadContactFrame>(completed.Frame).Contacts.Count);
        Assert.Equal(new uint[] { 1, 2, 3 }, completed.Frame!.Contacts.Select(contact => contact.Id).Order());
    }

    [Fact]
    public void ChangedScanOrDeviceCancelsAnIncompleteContactFrame()
    {
        var assembler = new TouchpadFrameAssembler();
        assembler.Process(Report(100, 7, 2, Contact(1, 0.1)));
        var nextScan = assembler.Process(Report(101, 8, 2, Contact(2, 0.9)));
        var nextDevice = assembler.Process(Report(102, 8, 2, 12, Contact(3, 0.2)));

        Assert.Null(nextScan.Frame);
        Assert.True(nextScan.DiscardedIncompleteFrame);
        Assert.Null(nextDevice.Frame);
        Assert.True(nextDevice.DiscardedIncompleteFrame);
    }

    [Fact]
    public void MalformedFramesAndExplicitCancellationDiscardPendingContacts()
    {
        var assembler = new TouchpadFrameAssembler();
        assembler.Process(Report(100, 7, 2, Contact(1, 0.1)));
        Assert.True(assembler.Cancel());

        var duplicate = assembler.Process(Report(110, 8, 2, Contact(2, 0.2), Contact(2, 0.3)));
        var tooMany = assembler.Process(Report(120, 9, 1, Contact(3, 0.2), Contact(4, 0.4)));

        Assert.True(duplicate.Malformed);
        Assert.True(duplicate.DiscardedIncompleteFrame);
        Assert.True(tooMany.Malformed);
    }

    private static TouchpadContactReport Report(long timestamp, uint scanTime, int expectedCount,
        params TouchpadContact[] contacts) => Report(timestamp, scanTime, expectedCount, 11, contacts);

    private static TouchpadContactReport Report(long timestamp, uint scanTime, int expectedCount, nint device,
        params TouchpadContact[] contacts) =>
        new(device, timestamp, scanTime, expectedCount, contacts);

    private static TouchpadContact Contact(uint id, double x) => new(id, true, x, 0.5);
}