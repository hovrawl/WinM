using WinManager.Windows;
using Xunit;

namespace WinManager.Windows.Tests;

public sealed class TouchpadCapabilityTests
{
    [Theory]
    [InlineData(TouchpadCapabilityState.Disabled, false, false, false)]
    [InlineData(TouchpadCapabilityState.UnsupportedDevice, false, false, false)]
    [InlineData(TouchpadCapabilityState.RegistrationFailed, true, false, false)]
    [InlineData(TouchpadCapabilityState.DecodingFailed, true, true, false)]
    [InlineData(TouchpadCapabilityState.GestureConflictBlocked, true, true, false)]
    [InlineData(TouchpadCapabilityState.Available, false, true, false)]
    [InlineData(TouchpadCapabilityState.Available, true, false, false)]
    [InlineData(TouchpadCapabilityState.Available, true, true, true)]
    public void ActionsRequireAvailableContactReportsAndVerifiedShellExclusion(TouchpadCapabilityState state,
        bool contactsObserved, bool conflictSuppressionVerified, bool canDispatch)
    {
        var result = new TouchpadCapabilityResult(state, 1, contactsObserved, conflictSuppressionVerified,
            "diagnostic");

        Assert.Equal(canDispatch, result.CanDispatchActions);
    }

    [Fact]
    public void AvailableStateWithoutADeviceCannotDispatchActions()
    {
        var result = new TouchpadCapabilityResult(TouchpadCapabilityState.Available, 0, true, true, "invalid");

        Assert.False(result.CanDispatchActions);
    }
}