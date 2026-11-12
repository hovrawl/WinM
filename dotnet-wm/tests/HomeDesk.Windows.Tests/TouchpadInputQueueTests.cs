using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class TouchpadInputQueueTests
{
    [Fact]
    public void QueueOverflowMarksTheNextAcceptedReportForRecognizerReset()
    {
        var queue = new TouchpadInputQueue<int>(1);

        Assert.True(queue.TryPublish(1));
        Assert.False(queue.TryPublish(2));
        Assert.True(queue.TryRead(out var earlier));
        Assert.Equal(1, earlier.Value);
        Assert.False(earlier.ResetBefore);
        Assert.True(queue.TryPublish(3));
        Assert.True(queue.TryRead(out var afterLoss));

        Assert.Equal(3, afterLoss.Value);
        Assert.True(afterLoss.ResetBefore);
        Assert.False(queue.TryRead(out _));
    }

    [Fact]
    public void ExplicitInputLossCoalescesUntilAReportCanBeQueued()
    {
        var queue = new TouchpadInputQueue<int>(1);
        queue.MarkInputLost();
        queue.MarkInputLost();

        Assert.True(queue.TryPublish(5));
        Assert.True(queue.TryRead(out var report));
        Assert.True(report.ResetBefore);
        Assert.Equal(5, report.Value);
    }
}