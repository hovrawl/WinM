using WinManager.Contracts;

namespace WinManager.Windows;

public sealed class TouchpadGestureRecognizer
{
    private const long MaximumFrameGapMilliseconds = 120;
    private const long FingerSettleMilliseconds = 35;
    private const double MinimumSwipeTravel = 0.16;
    private const double DominantAxisRatio = 1.35;
    private readonly bool completeThreeFingerSwipesOnFirstLift;
    private readonly Dictionary<uint, TouchpadContact> trackedContacts = [];
    private nint deviceHandle;
    private long lastTimestamp;
    private long settlingStarted;
    private int candidateFingerCount;
    private int stableFingerCount;
    private double travelX;
    private double travelY;
    private bool active;
    private bool settled;
    private bool drainingContacts;

    public TouchpadGestureRecognizer(TouchpadOptions? options = null)
    {
        completeThreeFingerSwipesOnFirstLift = options?.CompleteThreeFingerSwipesOnFirstLift ?? true;
    }

    public IReadOnlyList<TouchpadGesture> Process(TouchpadContactFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.TimestampMilliseconds < 0 || frame.ExpectedContactCount is < 0 or > 10 ||
            frame.Contacts is null || frame.Contacts.Count > 10 || frame.ExpectedContactCount != frame.Contacts.Count ||
            frame.Contacts.Any(contact => !double.IsFinite(contact.X) || !double.IsFinite(contact.Y) ||
                                          contact.X is < 0 or > 1 || contact.Y is < 0 or > 1) ||
            frame.Contacts.Select(contact => contact.Id).Distinct().Count() != frame.Contacts.Count)
        {
            Reset();
            return [];
        }

        if (active && (frame.DeviceHandle != deviceHandle || frame.TimestampMilliseconds < lastTimestamp ||
                       frame.TimestampMilliseconds - lastTimestamp > MaximumFrameGapMilliseconds))
            Reset();

        lastTimestamp = frame.TimestampMilliseconds;
        var activeContacts = frame.Contacts.Where(contact => contact.IsTouching).ToArray();
        if (CanSettleThreeFingerCandidateAtRelease(frame, activeContacts))
        {
            stableFingerCount = candidateFingerCount;
            settled = true;
            var completed = Complete(frame.TimestampMilliseconds);
            if (activeContacts.Length == 0)
                Reset();
            else
                drainingContacts = true;
            return completed;
        }

        if (activeContacts.Length == 0)
        {
            var completed = drainingContacts ? [] : Complete(frame.TimestampMilliseconds);
            Reset();
            return completed;
        }

        if (drainingContacts) return [];

        if (!active)
        {
            Begin(frame, activeContacts);
            return [];
        }

        var fingerCount = activeContacts.Length;
        var hasUntrackedContact = activeContacts.Any(contact => !trackedContacts.ContainsKey(contact.Id));
        if (!settled)
        {
            if (fingerCount != candidateFingerCount || hasUntrackedContact)
            {
                BeginCandidate(frame, activeContacts);
                return [];
            }

            if (!TryUpdateTravel(activeContacts, out var deltaX, out _)) return [];
            if (frame.TimestampMilliseconds - settlingStarted < FingerSettleMilliseconds) return [];

            stableFingerCount = candidateFingerCount;
            settled = true;
            return HorizontalDelta(deltaX, fingerCount, frame.TimestampMilliseconds);
        }

        if (fingerCount > stableFingerCount || hasUntrackedContact)
        {
            BeginCandidate(frame, activeContacts);
            return [];
        }

        if (completeThreeFingerSwipesOnFirstLift && stableFingerCount == 3 && fingerCount < 3)
        {
            var completed = Complete(frame.TimestampMilliseconds);
            drainingContacts = true;
            return completed;
        }

        if (!TryUpdateTravel(activeContacts, out var movementX, out _) || fingerCount < stableFingerCount)
            return [];

        return HorizontalDelta(movementX, fingerCount, frame.TimestampMilliseconds);
    }

    public void Cancel() => Reset();

    private void Begin(TouchpadContactFrame frame, IReadOnlyList<TouchpadContact> contacts)
    {
        active = true;
        deviceHandle = frame.DeviceHandle;
        candidateFingerCount = contacts.Count;
        stableFingerCount = 0;
        settlingStarted = frame.TimestampMilliseconds;
        ResetTravel();
        trackedContacts.Clear();
        foreach (var contact in contacts) trackedContacts.Add(contact.Id, contact);
    }

    private void BeginCandidate(TouchpadContactFrame frame, IReadOnlyList<TouchpadContact> contacts)
    {
        candidateFingerCount = contacts.Count;
        stableFingerCount = 0;
        settlingStarted = frame.TimestampMilliseconds;
        settled = false;
        ResetTravel();
        trackedContacts.Clear();
        foreach (var contact in contacts) trackedContacts.Add(contact.Id, contact);
    }

    private IReadOnlyList<TouchpadGesture> HorizontalDelta(double deltaX, int fingerCount, long timestamp)
    {
        return stableFingerCount == 2 && fingerCount == 2 && Math.Abs(deltaX) > 0.0001
            ? [new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 2, null, deltaX, timestamp)]
            : [];
    }

    private bool TryUpdateTravel(IReadOnlyList<TouchpadContact> contacts, out double deltaX, out double deltaY)
    {
        deltaX = deltaY = 0;
        var count = 0;
        foreach (var contact in contacts)
        {
            if (!trackedContacts.TryGetValue(contact.Id, out var previous)) continue;
            deltaX += contact.X - previous.X;
            deltaY += contact.Y - previous.Y;
            count++;
        }

        if (count == 0) return false;
        deltaX /= count;
        deltaY /= count;
        travelX += deltaX;
        travelY += deltaY;
        UpdateTrackedContacts(contacts);
        return true;
    }

    private IReadOnlyList<TouchpadGesture> Complete(long timestamp)
    {
        if (!settled || stableFingerCount is not (3 or 4)) return [];
        var horizontal = Math.Abs(travelX);
        var vertical = Math.Abs(travelY);
        if (Math.Max(horizontal, vertical) < MinimumSwipeTravel) return [];
        if (horizontal >= vertical * DominantAxisRatio)
            return
            [
                new TouchpadGesture(TouchpadGestureKind.Swipe, stableFingerCount,
                    travelX < 0 ? TouchpadSwipeDirection.Left : TouchpadSwipeDirection.Right, 0, timestamp)
            ];
        if (vertical >= horizontal * DominantAxisRatio)
            return
            [
                new TouchpadGesture(TouchpadGestureKind.Swipe, stableFingerCount,
                    travelY < 0 ? TouchpadSwipeDirection.Up : TouchpadSwipeDirection.Down, 0, timestamp)
            ];
        return [];
    }

    private bool CanSettleThreeFingerCandidateAtRelease(TouchpadContactFrame frame,
        IReadOnlyList<TouchpadContact> activeContacts) =>
        completeThreeFingerSwipesOnFirstLift && active && !settled && candidateFingerCount == 3 &&
        activeContacts.Count < candidateFingerCount &&
        frame.TimestampMilliseconds - settlingStarted >= FingerSettleMilliseconds &&
        frame.Contacts.All(contact => trackedContacts.ContainsKey(contact.Id));

    private void ResetTravel() => travelX = travelY = 0;

    private void UpdateTrackedContacts(IReadOnlyList<TouchpadContact> contacts)
    {
        foreach (var contact in contacts)
            if (trackedContacts.ContainsKey(contact.Id))
                trackedContacts[contact.Id] = contact;
    }

    private void Reset()
    {
        active = false;
        settled = false;
        deviceHandle = 0;
        lastTimestamp = 0;
        candidateFingerCount = 0;
        stableFingerCount = 0;
        travelX = travelY = 0;
        drainingContacts = false;
        trackedContacts.Clear();
    }
}