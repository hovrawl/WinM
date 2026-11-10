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

    [Theory]
    [InlineData(TouchpadCapabilityState.GestureConflictBlocked, 1, true, true)]
    [InlineData(TouchpadCapabilityState.Available, 1, true, true)]
    [InlineData(TouchpadCapabilityState.Disabled, 1, true, false)]
    [InlineData(TouchpadCapabilityState.UnsupportedDevice, 0, true, false)]
    [InlineData(TouchpadCapabilityState.RegistrationFailed, 1, true, false)]
    [InlineData(TouchpadCapabilityState.DecodingFailed, 1, true, false)]
    [InlineData(TouchpadCapabilityState.GestureConflictBlocked, 0, true, false)]
    [InlineData(TouchpadCapabilityState.GestureConflictBlocked, 1, false, false)]
    public void ContactRecognitionDoesNotRequireExclusiveShellSuppression(TouchpadCapabilityState state,
        int deviceCount, bool contactsObserved, bool canRecognize)
    {
        var result = new TouchpadCapabilityResult(state, deviceCount, contactsObserved, false, "diagnostic");

        Assert.Equal(canRecognize, result.CanRecognizeGestures);
        Assert.False(result.CanDispatchActions);
    }

    [Fact]
    public void AvailableStateWithoutADeviceCannotDispatchActions()
    {
        var result = new TouchpadCapabilityResult(TouchpadCapabilityState.Available, 0, true, true, "invalid");

        Assert.False(result.CanDispatchActions);
    }
}