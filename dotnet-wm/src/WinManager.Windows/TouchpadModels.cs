namespace WinManager.Windows;

public enum TouchpadCapabilityState
{
    Disabled,
    UnsupportedDevice,
    RegistrationFailed,
    DecodingFailed,
    GestureConflictBlocked,
    Available
}

public sealed record TouchpadCapabilityResult(
    TouchpadCapabilityState State,
    int DeviceCount,
    bool ContactReportsObserved,
    bool GestureConflictSuppressionVerified,
    string? Detail)
{
    public bool CanDispatchActions => State == TouchpadCapabilityState.Available && DeviceCount > 0 &&
                                      ContactReportsObserved && GestureConflictSuppressionVerified;
}

public readonly record struct TouchpadContact(uint Id, bool IsConfident, double X, double Y);

public sealed record TouchpadContactFrame(
    nint DeviceHandle,
    long TimestampMilliseconds,
    uint ScanTime,
    int ExpectedContactCount,
    IReadOnlyList<TouchpadContact> Contacts);

public sealed record TouchpadContactReport(
    nint DeviceHandle,
    long TimestampMilliseconds,
    uint ScanTime,
    int ExpectedContactCount,
    IReadOnlyList<TouchpadContact> Contacts);

public enum TouchpadGestureKind
{
    Swipe,
    HorizontalDelta
}

public enum TouchpadSwipeDirection
{
    Left,
    Right,
    Up,
    Down
}

public sealed record TouchpadGesture(
    TouchpadGestureKind Kind,
    int FingerCount,
    TouchpadSwipeDirection? Direction,
    double HorizontalDelta,
    long TimestampMilliseconds);