using HomeDesk.Contracts;
using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class TouchpadGestureRecognizerTests
{
    [Fact]
    public void ThreeFingerHorizontalSwipeIsCommittedOnceAtContactEnd()
    {
        var recognizer = new TouchpadGestureRecognizer();

        Assert.Empty(recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7)));
        Assert.Empty(recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7)));
        Assert.Empty(recognizer.Process(Frame(56, 3, 0.25, 0.45, 0.65)));
        Assert.Empty(recognizer.Process(Frame(72, 3, 0.1, 0.3, 0.5)));
        var completed = recognizer.Process(Frame(88, 0));

        var gesture = Assert.Single(completed);
        Assert.Equal(TouchpadGestureKind.Swipe, gesture.Kind);
        Assert.Equal(3, gesture.FingerCount);
        Assert.Equal(TouchpadSwipeDirection.Left, gesture.Direction);
        Assert.Empty(recognizer.Process(Frame(104, 0)));
    }

    [Fact]
    public void ThreeFingerSwipeCompletesOnTipLiftOffWithoutAnEmptyContactFrame()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.1, 0.3, 0.5));

        var completed = recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.1, 0.5, false),
            new TouchpadContact(2, true, 0.3, 0.5, false),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        var gesture = Assert.Single(completed);
        Assert.Equal(3, gesture.FingerCount);
        Assert.Equal(TouchpadSwipeDirection.Left, gesture.Direction);
        Assert.Empty(recognizer.Process(Frame(88, 0)));
    }

    [Fact]
    public void ThreeFingerSwipeCompletesOnFirstTipLift()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.1, 0.3, 0.5));

        var completed = recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(completed).Direction);
    }

    [Fact]
    public void ThreeFingerSwipeCompletesWhenFirstLiftIsFirstFrameAfterSettleInterval()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(16, 3, 0.1, 0.3, 0.5));

        var completed = recognizer.Process(FrameFromContacts(40, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        var gesture = Assert.Single(completed);
        Assert.Equal(3, gesture.FingerCount);
        Assert.Equal(TouchpadSwipeDirection.Left, gesture.Direction);
        Assert.Empty(recognizer.Process(FrameFromContacts(56, 3,
            new TouchpadContact(1, true, 0.05, 0.5),
            new TouchpadContact(2, true, 0.25, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false))));
        Assert.Empty(recognizer.Process(Frame(72, 0)));
    }

    [Fact]
    public void SimultaneousLiftCanSettleAndCompleteTheCandidate()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(16, 3, 0.1, 0.3, 0.5));

        var completed = recognizer.Process(FrameFromContacts(40, 3,
            new TouchpadContact(1, true, 0.1, 0.5, false),
            new TouchpadContact(2, true, 0.3, 0.5, false),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(completed).Direction);
    }

    [Fact]
    public void FirstLiftBeforeSettleIntervalDoesNotPublishCandidate()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(16, 3, 0.1, 0.3, 0.5));

        Assert.Empty(recognizer.Process(FrameFromContacts(34, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false))));
        Assert.Empty(recognizer.Process(Frame(50, 0)));
    }

    [Fact]
    public void FirstLiftDoesNotSettleAReplacementContactOrFourFingerCandidate()
    {
        var replacementRecognizer = new TouchpadGestureRecognizer();
        replacementRecognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        replacementRecognizer.Process(Frame(16, 3, 0.1, 0.3, 0.5));
        Assert.Empty(replacementRecognizer.Process(FrameFromContacts(40, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(4, true, 0.5, 0.5))));
        Assert.Empty(replacementRecognizer.Process(Frame(56, 0)));

        var fourFingerRecognizer = new TouchpadGestureRecognizer();
        fourFingerRecognizer.Process(Frame(0, 4, 0.2, 0.4, 0.6, 0.8));
        fourFingerRecognizer.Process(Frame(16, 4, 0.05, 0.25, 0.45, 0.65));
        Assert.Empty(fourFingerRecognizer.Process(FrameFromContacts(40, 4,
            new TouchpadContact(1, true, 0.05, 0.5),
            new TouchpadContact(2, true, 0.25, 0.5),
            new TouchpadContact(3, true, 0.45, 0.5),
            new TouchpadContact(4, true, 0.65, 0.5, false))));
    }

    [Fact]
    public void FirstLiftCompletionDrainsTheContactTailAndAllowsTheNextSwipe()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.1, 0.3, 0.5));
        Assert.Single(recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false))));

        Assert.Empty(recognizer.Process(FrameFromContacts(88, 3,
            new TouchpadContact(1, true, 0.05, 0.5),
            new TouchpadContact(2, true, 0.25, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false))));
        Assert.Empty(recognizer.Process(FrameFromContacts(104, 3,
            new TouchpadContact(1, true, 0.05, 0.5, false),
            new TouchpadContact(2, true, 0.25, 0.5, false),
            new TouchpadContact(3, true, 0.5, 0.5, false))));

        recognizer.Process(Frame(120, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(160, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(176, 3, 0.5, 0.7, 0.9));
        var nextSwipe = recognizer.Process(FrameFromContacts(192, 3,
            new TouchpadContact(1, true, 0.5, 0.5),
            new TouchpadContact(2, true, 0.7, 0.5),
            new TouchpadContact(3, true, 0.9, 0.5, false)));

        Assert.Equal(TouchpadSwipeDirection.Right, Assert.Single(nextSwipe).Direction);
    }

    [Fact]
    public void FirstLiftFrameDoesNotContributeTravelToThreshold()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.25, 0.45, 0.65));

        var completion = recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        Assert.Empty(completion);
        Assert.Empty(recognizer.Process(Frame(88, 0)));
    }

    [Fact]
    public void FirstLiftPublishesThreeFingerVerticalSwipeForSwitcherRouting()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(FramePoints(0, (0.5, 0.3), (0.5, 0.5), (0.5, 0.7)));
        recognizer.Process(FramePoints(40, (0.5, 0.3), (0.5, 0.5), (0.5, 0.7)));
        recognizer.Process(FramePoints(56, (0.5, 0.1), (0.5, 0.3), (0.5, 0.5)));

        var completed = recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.5, 0.1),
            new TouchpadContact(2, true, 0.5, 0.3),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        Assert.Equal(TouchpadSwipeDirection.Up, Assert.Single(completed).Direction);
    }

    [Fact]
    public void DisabledFirstLiftOptionPreservesWaitForAllTipsBehavior()
    {
        var recognizer = new TouchpadGestureRecognizer(new TouchpadOptions(
            CompleteThreeFingerSwipesOnFirstLift: false));
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.1, 0.3, 0.5));

        Assert.Empty(recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false))));
        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(recognizer.Process(Frame(88, 0))).Direction);
    }

    [Fact]
    public void MovementDuringFingerSettlingContributesToTheCompletedSwipe()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(16, 3, 0.12, 0.32, 0.52));
        recognizer.Process(Frame(40, 3, 0.12, 0.32, 0.52));

        var completed = recognizer.Process(Frame(56, 0));

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(completed).Direction);
    }

    [Fact]
    public void StaggeredTipLiftsRetainMovementAndPublishOnlyWhenAllFingersLift()
    {
        var recognizer = new TouchpadGestureRecognizer(new TouchpadOptions(
            CompleteThreeFingerSwipesOnFirstLift: false));
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.25, 0.45, 0.65));
        recognizer.Process(Frame(72, 3, 0.1, 0.3, 0.5));
        Assert.Empty(recognizer.Process(FrameFromContacts(88, 3,
            new TouchpadContact(1, true, 0.08, 0.5),
            new TouchpadContact(2, true, 0.28, 0.5),
            new TouchpadContact(3, true, 0.5, 0.5, false))));
        Assert.Empty(recognizer.Process(FrameFromContacts(104, 3,
            new TouchpadContact(1, true, 0.05, 0.5),
            new TouchpadContact(2, true, 0.28, 0.5, false),
            new TouchpadContact(3, true, 0.5, 0.5, false))));

        var completed = recognizer.Process(FrameFromContacts(120, 3,
            new TouchpadContact(1, true, 0.05, 0.5, false),
            new TouchpadContact(2, true, 0.28, 0.5, false),
            new TouchpadContact(3, true, 0.5, 0.5, false)));

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(completed).Direction);
        Assert.Empty(recognizer.Process(FrameFromContacts(136, 3,
            new TouchpadContact(1, true, 0.05, 0.5, false),
            new TouchpadContact(2, true, 0.28, 0.5, false),
            new TouchpadContact(3, true, 0.5, 0.5, false))));
    }

    [Fact]
    public void MalformedTipReleaseCancelsInsteadOfFinalizingTheSwipe()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.1, 0.3, 0.5));
        Assert.Empty(recognizer.Process(FrameFromContacts(72, 3,
            new TouchpadContact(1, true, 0.1, 0.5),
            new TouchpadContact(2, true, 0.3, 0.5, false))));

        Assert.Empty(recognizer.Process(Frame(88, 0)));
    }

    [Fact]
    public void ThreeFingerSwipeSurvivesConsecutiveFramesWithTheSameMillisecondTimestamp()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(100, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(140, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(156, 3, 0.25, 0.45, 0.65));
        recognizer.Process(Frame(156, 3, 0.1, 0.3, 0.5));

        var completed = recognizer.Process(Frame(172, 0));

        var gesture = Assert.Single(completed);
        Assert.Equal(3, gesture.FingerCount);
        Assert.Equal(TouchpadSwipeDirection.Left, gesture.Direction);
    }

    [Fact]
    public void FourFingerTransitionCancelsThreeFingerCandidateAndFollowsSwipeDirection()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.1, 0.3, 0.5));
        Assert.Empty(recognizer.Process(Frame(64, 4, 0.2, 0.4, 0.6, 0.8)));
        Assert.Empty(recognizer.Process(Frame(104, 4, 0.2, 0.4, 0.6, 0.8)));
        Assert.Empty(recognizer.Process(Frame(120, 4, 0.4, 0.6, 0.8, 1.0)));

        var completed = recognizer.Process(Frame(136, 0));

        var gesture = Assert.Single(completed);
        Assert.Equal(4, gesture.FingerCount);
        Assert.Equal(TouchpadSwipeDirection.Right, gesture.Direction);
    }

    [Fact]
    public void TwoFingerMovementProducesOnlyContinuousHorizontalDeltas()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 2, 0.4, 0.6));
        recognizer.Process(Frame(40, 2, 0.4, 0.6));

        var movement = Assert.Single(recognizer.Process(Frame(56, 2, 0.5, 0.7)));
        Assert.Equal(TouchpadGestureKind.HorizontalDelta, movement.Kind);
        Assert.Equal(2, movement.FingerCount);
        Assert.Null(movement.Direction);
        Assert.True(movement.HorizontalDelta > 0);
        Assert.Empty(recognizer.Process(Frame(72, 0)));
    }

    [Fact]
    public void ThreeFingerVerticalSwipeIsRecognizedSeparatelyFromHorizontalFocus()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(FramePoints(0, (0.5, 0.3), (0.5, 0.5), (0.5, 0.7)));
        recognizer.Process(FramePoints(40, (0.5, 0.3), (0.5, 0.5), (0.5, 0.7)));
        recognizer.Process(FramePoints(56, (0.5, 0.25), (0.5, 0.45), (0.5, 0.65)));
        recognizer.Process(FramePoints(72, (0.5, 0.1), (0.5, 0.3), (0.5, 0.5)));

        var gesture = Assert.Single(recognizer.Process(Frame(88, 0)));

        Assert.Equal(3, gesture.FingerCount);
        Assert.Equal(TouchpadSwipeDirection.Up, gesture.Direction);
    }

    [Fact]
    public void DiagonalMovementDoesNotBecomeADirectionalSwipe()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(FramePoints(0, (0.3, 0.3), (0.5, 0.5), (0.7, 0.7)));
        recognizer.Process(FramePoints(40, (0.3, 0.3), (0.5, 0.5), (0.7, 0.7)));
        recognizer.Process(FramePoints(56, (0.25, 0.25), (0.45, 0.45), (0.65, 0.65)));
        recognizer.Process(FramePoints(72, (0.1, 0.1), (0.3, 0.3), (0.5, 0.5)));

        Assert.Empty(recognizer.Process(Frame(88, 0)));
    }

    [Fact]
    public void AmbiguousShortMalformedAndDiscontinuousInputNeverCompletesASwipe()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(40, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(56, 3, 0.28, 0.48, 0.68));
        Assert.Empty(recognizer.Process(Frame(72, 0)));

        recognizer.Process(Frame(100, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(140, 3, 0.3, 0.5, 0.7));
        recognizer.Process(Frame(156, 3, 0.2, 0.4, 0.6));
        Assert.Empty(recognizer.Process(Frame(300, 3, 0.1, 0.3, 0.5)));
        Assert.Empty(recognizer.Process(Frame(316, 0)));

        recognizer.Process(Frame(340, 3, 0.3, 0.5, 0.7));
        Assert.Empty(recognizer.Process(Frame(380, 3, 0.3, 0.5, 0.7)));
        Assert.Empty(recognizer.Process(new TouchpadContactFrame(42, 396, 3, 3,
        [
            new TouchpadContact(1, true, 1.2, 0.5), new TouchpadContact(2, true, 0.5, 0.5),
            new TouchpadContact(3, true, 0.7, 0.5)
        ])));
        Assert.Empty(recognizer.Process(Frame(412, 0)));
    }

    [Fact]
    public void InsufficientExpectedContactsCancelTheCurrentFrameSequence()
    {
        var recognizer = new TouchpadGestureRecognizer();
        recognizer.Process(Frame(0, 3, 0.3, 0.5, 0.7));
        var incomplete = new TouchpadContactFrame(42, 40, 3, 3,
            [new TouchpadContact(1, true, 0.3, 0.5), new TouchpadContact(2, true, 0.5, 0.5)]);

        Assert.Empty(recognizer.Process(incomplete));
        Assert.Empty(recognizer.Process(Frame(56, 0)));
    }

    private static TouchpadContactFrame Frame(long timestamp, int expectedCount, params double[] xPositions)
    {
        var contacts = xPositions.Select((x, index) => new TouchpadContact((uint)(index + 1), true, x, 0.5)).ToArray();
        return new TouchpadContactFrame(42, timestamp, (uint)timestamp, expectedCount, contacts);
    }

    private static TouchpadContactFrame FramePoints(long timestamp, params (double X, double Y)[] points)
    {
        var contacts = points.Select((point, index) =>
            new TouchpadContact((uint)(index + 1), true, point.X, point.Y)).ToArray();
        return new TouchpadContactFrame(42, timestamp, (uint)timestamp, points.Length, contacts);
    }

    private static TouchpadContactFrame FrameFromContacts(long timestamp, int expectedCount,
        params TouchpadContact[] contacts) =>
        new(42, timestamp, (uint)timestamp, expectedCount, contacts);
}