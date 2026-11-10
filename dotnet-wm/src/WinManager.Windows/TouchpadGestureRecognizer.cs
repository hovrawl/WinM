namespace WinManager.Windows;

public sealed class TouchpadGestureRecognizer
{
    private const long MaximumFrameGapMilliseconds = 120;
    private const long FingerSettleMilliseconds = 35;
    private const double MinimumSwipeTravel = 0.16;
    private const double DominantAxisRatio = 1.35;
    private readonly Dictionary<uint, TouchpadContact> trackedContacts = [];
    private nint deviceHandle;
    private long lastTimestamp;
    private long settlingStarted;
    private int candidateFingerCount;
    private int stableFingerCount;
    private double startX;
    private double startY;
    private double lastX;
    private double lastY;
    private double travelX;
    private double travelY;
    private bool active;
    private bool settled;

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
        if (frame.Contacts.Count == 0)
        {
            var completed = Complete(frame.TimestampMilliseconds);
            Reset();
            return completed;
        }

        if (!active)
        {
            Begin(frame);
            return [];
        }

        var fingerCount = frame.Contacts.Count;
        if (fingerCount > Math.Max(candidateFingerCount, stableFingerCount))
        {
            candidateFingerCount = fingerCount;
            settlingStarted = frame.TimestampMilliseconds;
            settled = false;
            ResetTravel(frame);
            trackedContacts.Clear();
            foreach (var contact in frame.Contacts) trackedContacts.Add(contact.Id, contact);
            return [];
        }

        if (!settled)
        {
            if (fingerCount != candidateFingerCount)
            {
                candidateFingerCount = fingerCount;
                settlingStarted = frame.TimestampMilliseconds;
                ResetTravel(frame);
                trackedContacts.Clear();
                foreach (var contact in frame.Contacts) trackedContacts.Add(contact.Id, contact);
                return [];
            }

            if (frame.TimestampMilliseconds - settlingStarted < FingerSettleMilliseconds)
            {
                UpdateTrackedContacts(frame.Contacts);
                return [];
            }

            stableFingerCount = candidateFingerCount;
            settled = true;
            ResetTravel(frame);
            trackedContacts.Clear();
            foreach (var contact in frame.Contacts) trackedContacts.Add(contact.Id, contact);
            return [];
        }

        if (fingerCount < stableFingerCount)
        {
            UpdateTrackedContacts(frame.Contacts);
            return [];
        }

        if (fingerCount != stableFingerCount)
        {
            Reset();
            Begin(frame);
            return [];
        }

        if (frame.Contacts.Any(contact => !trackedContacts.ContainsKey(contact.Id)))
        {
            candidateFingerCount = fingerCount;
            settlingStarted = frame.TimestampMilliseconds;
            stableFingerCount = 0;
            settled = false;
            ResetTravel(frame);
            trackedContacts.Clear();
            foreach (var contact in frame.Contacts) trackedContacts.Add(contact.Id, contact);
            return [];
        }

        if (!TryCentroid(frame.Contacts, trackedContacts.Keys, out var x, out var y)) return [];
        var deltaX = x - lastX;
        lastX = x;
        lastY = y;
        travelX = x - startX;
        travelY = y - startY;
        UpdateTrackedContacts(frame.Contacts);
        return stableFingerCount == 2 && Math.Abs(deltaX) > 0.0001
            ?
            [
                new TouchpadGesture(TouchpadGestureKind.HorizontalDelta, 2, null, deltaX,
                    frame.TimestampMilliseconds)
            ]
            : [];
    }

    public void Cancel() => Reset();

    private void Begin(TouchpadContactFrame frame)
    {
        active = true;
        deviceHandle = frame.DeviceHandle;
        candidateFingerCount = frame.Contacts.Count;
        stableFingerCount = 0;
        settlingStarted = frame.TimestampMilliseconds;
        ResetTravel(frame);
        trackedContacts.Clear();
        foreach (var contact in frame.Contacts) trackedContacts.Add(contact.Id, contact);
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

    private void ResetTravel(TouchpadContactFrame frame)
    {
        if (!TryCentroid(frame.Contacts, frame.Contacts.Select(contact => contact.Id), out startX, out startY))
            startX = startY = 0;
        lastX = startX;
        lastY = startY;
        travelX = travelY = 0;
    }

    private void UpdateTrackedContacts(IReadOnlyList<TouchpadContact> contacts)
    {
        foreach (var contact in contacts)
            if (trackedContacts.ContainsKey(contact.Id))
                trackedContacts[contact.Id] = contact;
    }

    private static bool TryCentroid(IReadOnlyList<TouchpadContact> contacts, IEnumerable<uint> contactIds,
        out double x, out double y)
    {
        x = y = 0;
        var count = 0;
        foreach (var id in contactIds)
        {
            foreach (var contact in contacts)
            {
                if (contact.Id != id) continue;
                x += contact.X;
                y += contact.Y;
                count++;
                break;
            }
        }

        if (count == 0) return false;
        x /= count;
        y /= count;
        return true;
    }

    private void Reset()
    {
        active = false;
        settled = false;
        deviceHandle = 0;
        lastTimestamp = 0;
        candidateFingerCount = 0;
        stableFingerCount = 0;
        startX = startY = lastX = lastY = travelX = travelY = 0;
        trackedContacts.Clear();
    }
}