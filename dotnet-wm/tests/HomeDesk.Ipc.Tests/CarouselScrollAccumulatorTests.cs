using HomeDeskManager;
using Xunit;

namespace HomeDesk.Ipc.Tests;

public sealed class CarouselScrollAccumulatorTests
{
    [Fact]
    public void AccumulatorCoalescesNativeDeltasIntoOneScheduledUiUpdate()
    {
        var scheduled = new Queue<Action>();
        var applied = new List<double>();
        var accumulator = new CarouselScrollAccumulator(scheduled.Enqueue, applied.Add);

        accumulator.Add(0.1);
        accumulator.Add(0.2);
        accumulator.Add(double.NaN);

        Assert.Single(scheduled);
        scheduled.Dequeue()();

        Assert.Equal(0.3, Assert.Single(applied), 6);
        Assert.Empty(scheduled);
    }
}