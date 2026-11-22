using HomeDesk.Windows;

namespace HomeDeskManager;

internal enum CarouselGestureAction
{
    Open,
    Accept,
    MinimizeSelected,
    RestoreSelected
}

internal static class SwitcherTouchpadPolicy
{
    public static bool TryGetScrollDelta(TouchpadGesture gesture, TouchpadCapabilityResult capability,
        out double horizontalDelta)
    {
        horizontalDelta = 0;
        if (!capability.CanRecognizeGestures || gesture.Kind != TouchpadGestureKind.HorizontalDelta ||
            gesture.FingerCount != 2 || !double.IsFinite(gesture.HorizontalDelta)) return false;
        horizontalDelta = -gesture.HorizontalDelta;
        return true;
    }

    public static CarouselGestureAction? Route(TouchpadGesture gesture, TouchpadCapabilityResult capability,
        bool minimizedRailFocused = false)
    {
        if (!capability.CanRecognizeGestures || gesture.Kind != TouchpadGestureKind.Swipe) return null;
        if (gesture.FingerCount == 2)
            return gesture.Direction switch
            {
                TouchpadSwipeDirection.Down when !minimizedRailFocused => CarouselGestureAction.MinimizeSelected,
                TouchpadSwipeDirection.Up when minimizedRailFocused => CarouselGestureAction.RestoreSelected,
                _ => null
            };
        if (gesture.FingerCount != 3) return null;
        return gesture.Direction switch
        {
            TouchpadSwipeDirection.Up => CarouselGestureAction.Open,
            TouchpadSwipeDirection.Down => CarouselGestureAction.Accept,
            _ => null
        };
    }
}