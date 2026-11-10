namespace WinManager.Windows;

public readonly record struct TouchpadFrameAssemblyResult(
    TouchpadContactFrame? Frame,
    bool DiscardedIncompleteFrame,
    bool Malformed);

public sealed class TouchpadFrameAssembler
{
    private const long MaximumFrameAssemblyMilliseconds = 80;
    private readonly Dictionary<uint, TouchpadContact> contacts = [];
    private nint deviceHandle;
    private long startedAt;
    private uint scanTime;
    private int expectedContactCount;
    private bool pending;

    public TouchpadFrameAssemblyResult Process(TouchpadContactReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.DeviceHandle == 0 || report.TimestampMilliseconds < 0 ||
            report.ExpectedContactCount is < 0 or > 10 ||
            report.Contacts is null || report.Contacts.Count > report.ExpectedContactCount ||
            report.Contacts.Count > 10 || report.Contacts.Any(contact => !double.IsFinite(contact.X) ||
                                                                         !double.IsFinite(contact.Y) ||
                                                                         contact.X is < 0 or > 1 ||
                                                                         contact.Y is < 0 or > 1) ||
            report.Contacts.Select(contact => contact.Id).Distinct().Count() != report.Contacts.Count)
        {
            Reset();
            return new(null, true, true);
        }

        var discarded = false;
        if (pending && (report.DeviceHandle != deviceHandle || report.ScanTime != scanTime ||
                        report.ExpectedContactCount != expectedContactCount ||
                        report.TimestampMilliseconds < startedAt ||
                        report.TimestampMilliseconds - startedAt > MaximumFrameAssemblyMilliseconds))
        {
            discarded = contacts.Count < expectedContactCount && expectedContactCount > 0;
            Reset();
        }

        if (!pending)
        {
            if (report.ExpectedContactCount == 0)
                return new(new TouchpadContactFrame(report.DeviceHandle, report.TimestampMilliseconds, report.ScanTime,
                    0, []), discarded, false);
            pending = true;
            deviceHandle = report.DeviceHandle;
            startedAt = report.TimestampMilliseconds;
            scanTime = report.ScanTime;
            expectedContactCount = report.ExpectedContactCount;
        }

        foreach (var contact in report.Contacts) contacts[contact.Id] = contact;
        if (contacts.Count > expectedContactCount)
        {
            Reset();
            return new(null, true, true);
        }

        if (contacts.Count < expectedContactCount) return new(null, discarded, false);
        var frame = new TouchpadContactFrame(deviceHandle, report.TimestampMilliseconds, scanTime,
            expectedContactCount, contacts.Values.ToArray());
        Reset();
        return new(frame, discarded, false);
    }

    public bool Cancel()
    {
        var discarded = pending && contacts.Count < expectedContactCount;
        Reset();
        return discarded;
    }

    private void Reset()
    {
        contacts.Clear();
        deviceHandle = 0;
        startedAt = 0;
        scanTime = 0;
        expectedContactCount = 0;
        pending = false;
    }
}