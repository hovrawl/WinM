using HomeDeskManager;
using Xunit;

namespace HomeDesk.Ipc.Tests;

public sealed class CarouselScrollMotionTests
{
    [Fact]
    public void ScrollMotionAcceleratesDeceleratesReversesAndStops()
    {
        var motion = new CarouselScrollMotion();
        motion.AddInput(50);
        var initialVelocity = motion.Velocity;
        motion.AddInput(50);
        var acceleratedVelocity = motion.Velocity;

        Assert.True(acceleratedVelocity > initialVelocity);
        Assert.True(motion.Advance(0.1) > 0);
        Assert.True(motion.Velocity < acceleratedVelocity);

        motion.AddInput(-200);
        Assert.True(motion.Velocity < 0);
        motion.Stop();

        Assert.False(motion.IsActive);
        Assert.Equal(0, motion.Advance(0.1));
    }
}
