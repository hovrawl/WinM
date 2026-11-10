using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinManager.Windows;

internal sealed record TouchpadValueRange(
    ushort UsagePage,
    ushort Usage,
    ushort LinkCollection,
    int LogicalMinimum,
    int LogicalMaximum);

internal sealed record TouchpadContactCollection(
    ushort LinkCollection,
    byte ReportId,
    TouchpadValueRange ContactId,
    TouchpadValueRange X,
    TouchpadValueRange Y);

internal sealed record TouchpadHidReportLayout(
    ushort InputReportLength,
    byte ReportId,
    IReadOnlyList<TouchpadContactCollection> ContactCollections,
    TouchpadValueRange? ContactCount,
    TouchpadValueRange? ScanTime);

internal interface IHidReportValueReader
{
    bool TryGetValue(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value);

    bool TryGetUsages(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
        out IReadOnlyList<ushort> usages);
}

internal sealed class TouchpadHidDecoder(
    TouchpadHidReportLayout layout,
    nint preparsedData,
    IHidReportValueReader valueReader)
{
    private const ushort DigitizerUsagePage = 0x0d;
    private const ushort TipSwitchUsage = 0x42;
    private const ushort ContactCountUsage = 0x54;
    private const ushort ScanTimeUsage = 0x56;

    public bool TryDecode(nint deviceHandle, long timestampMilliseconds, byte[] report,
        out TouchpadContactReport? decoded)
    {
        decoded = null;
        if (deviceHandle == 0 || timestampMilliseconds < 0 || report is null || report.Length < 2 ||
            report.Length > layout.InputReportLength ||
            layout.ContactCollections.Count is < 1 or > 10 || preparsedData == 0)
            return false;
        if (layout.ReportId != 0 && report[0] != layout.ReportId) return true;

        var expectedCount = 0;
        if (layout.ContactCount is { } countRange)
        {
            if (!valueReader.TryGetValue(preparsedData, report, countRange.UsagePage, countRange.LinkCollection,
                    ContactCountUsage, out var value) || value > 10)
                return false;
            expectedCount = (int)value;
        }

        var scanTime = 0u;
        if (layout.ScanTime is { } scanRange)
        {
            if (!valueReader.TryGetValue(preparsedData, report, scanRange.UsagePage, scanRange.LinkCollection,
                    ScanTimeUsage, out scanTime)) return false;
        }

        var contacts = new List<TouchpadContact>(Math.Max(1, expectedCount));
        foreach (var collection in layout.ContactCollections)
        {
            if (!valueReader.TryGetUsages(preparsedData, report, DigitizerUsagePage, collection.LinkCollection,
                    out var activeUsages)) return false;
            if (!activeUsages.Contains(TipSwitchUsage)) continue;
            if (!valueReader.TryGetValue(preparsedData, report, collection.ContactId.UsagePage,
                    collection.ContactId.LinkCollection,
                    collection.ContactId.Usage, out var id) ||
                id < collection.ContactId.LogicalMinimum || id > collection.ContactId.LogicalMaximum ||
                !valueReader.TryGetValue(preparsedData, report, collection.X.UsagePage, collection.X.LinkCollection,
                    collection.X.Usage, out var x) ||
                !valueReader.TryGetValue(preparsedData, report, collection.Y.UsagePage, collection.Y.LinkCollection,
                    collection.Y.Usage, out var y) ||
                !TryNormalize(x, collection.X, out var normalizedX) ||
                !TryNormalize(y, collection.Y, out var normalizedY))
                return false;

            if (contacts.Any(contact => contact.Id == id)) return false;
            contacts.Add(new TouchpadContact(id, true, normalizedX, normalizedY));
        }

        if (layout.ContactCount is null) expectedCount = contacts.Count;
        if (contacts.Count > expectedCount) return false;
        decoded = new TouchpadContactReport(deviceHandle, timestampMilliseconds, scanTime, expectedCount, contacts);
        return true;
    }

    private static bool TryNormalize(uint value, TouchpadValueRange range, out double normalized)
    {
        normalized = 0;
        if (range.LogicalMaximum <= range.LogicalMinimum || value < range.LogicalMinimum ||
            value > range.LogicalMaximum) return false;
        normalized = (value - range.LogicalMinimum) / (double)(range.LogicalMaximum - range.LogicalMinimum);
        return double.IsFinite(normalized) && normalized is >= 0 and <= 1;
    }
}

internal sealed class RawHidTouchpadDevice : IDisposable
{
    private const ushort DigitizerUsagePage = 0x0d;
    private const ushort TouchPadUsage = 0x05;
    private const ushort ContactIdUsage = 0x51;
    private const ushort ContactCountUsage = 0x54;
    private const ushort ScanTimeUsage = 0x56;
    private const ushort XUsage = 0x30;
    private const ushort YUsage = 0x31;
    private const uint RawInputDeviceInfoPreparsedData = 0x20000005;
    private const uint HidPStatusSuccess = 0x00110000;

    private readonly nint preparsedData;
    private readonly int inputReportLength;
    private bool disposed;

    private RawHidTouchpadDevice(nint handle, nint preparsedData, int inputReportLength,
        TouchpadHidDecoder decoder)
    {
        Handle = handle;
        this.preparsedData = preparsedData;
        this.inputReportLength = inputReportLength;
        Decoder = decoder;
    }

    public nint Handle { get; }
    public TouchpadHidDecoder Decoder { get; }
    public int InputReportLength => inputReportLength;

    internal static (int Caps, int ValueCaps) NativeStructureSizes =>
        (Marshal.SizeOf<HidpCaps>(), Marshal.SizeOf<HidpValueCaps>());

    public static bool TryCreate(nint handle, out RawHidTouchpadDevice? device, out string failure)
    {
        device = null;
        failure = "unsupported_device";
        if (handle == 0) return false;
        uint preparsedLength = 0;
        if (GetRawInputDeviceInfo(handle, RawInputDeviceInfoPreparsedData, 0, ref preparsedLength) == uint.MaxValue ||
            preparsedLength is 0 or > 1048576)
        {
            failure = "descriptor_unavailable";
            return false;
        }

        var preparsedData = Marshal.AllocHGlobal((int)preparsedLength);
        try
        {
            var actualLength = preparsedLength;
            if (GetRawInputDeviceInfo(handle, RawInputDeviceInfoPreparsedData, preparsedData, ref actualLength) ==
                uint.MaxValue || actualLength != preparsedLength)
            {
                failure = "descriptor_unavailable";
                return false;
            }

            if (HidP_GetCaps(preparsedData, out var caps) != HidPStatusSuccess ||
                caps.UsagePage != DigitizerUsagePage || caps.Usage != TouchPadUsage ||
                caps.InputReportByteLength < 2 || caps.NumberInputValueCaps is 0 or > 512)
            {
                failure = "unsupported_descriptor";
                return false;
            }

            var valueCaps = ReadValueCaps(preparsedData, caps.NumberInputValueCaps);
            var contactCollections = BuildContactCollections(valueCaps);
            if (contactCollections.Count is < 1 or > 10)
            {
                failure = "unsupported_descriptor";
                return false;
            }

            var reportId = contactCollections[0].ReportId;
            var matchingContacts = contactCollections.Where(collection => collection.ReportId == reportId).ToArray();
            var contactCount = valueCaps.Where(cap => cap.HasUsage(ContactCountUsage) &&
                                                      cap.UsagePage == DigitizerUsagePage && cap.ReportId == reportId)
                .Select(cap => (HidpValueCaps?)cap).FirstOrDefault();
            var scanTime = valueCaps.Where(cap => cap.HasUsage(ScanTimeUsage) &&
                                                  cap.UsagePage == DigitizerUsagePage && cap.ReportId == reportId)
                .Select(cap => (HidpValueCaps?)cap).FirstOrDefault();
            if (matchingContacts.Length is < 1 or > 10 || contactCount is null)
            {
                failure = "unsupported_descriptor";
                return false;
            }

            var layout = new TouchpadHidReportLayout(caps.InputReportByteLength, reportId, matchingContacts,
                contactCount.Value.ToRange(ContactCountUsage), scanTime?.ToRange(ScanTimeUsage));
            var decoder = new TouchpadHidDecoder(layout, preparsedData, new WindowsHidReportValueReader());
            device = new RawHidTouchpadDevice(handle, preparsedData, caps.InputReportByteLength, decoder);
            preparsedData = 0;
            failure = string.Empty;
            return true;
        }
        catch (Exception error) when (error is ExternalException or DllNotFoundException or EntryPointNotFoundException
                                          or Win32Exception or InvalidOperationException)
        {
            failure = "descriptor_decode_failed";
            return false;
        }
        finally
        {
            if (preparsedData != 0) Marshal.FreeHGlobal(preparsedData);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Marshal.FreeHGlobal(preparsedData);
    }

    private static IReadOnlyList<HidpValueCaps> ReadValueCaps(nint preparsedData, ushort count)
    {
        var size = Marshal.SizeOf<HidpValueCaps>();
        var buffer = Marshal.AllocHGlobal(checked(size * count));
        try
        {
            var actualCount = (uint)count;
            if (HidP_GetValueCaps(HidpReportType.Input, buffer, ref actualCount, preparsedData) !=
                HidPStatusSuccess || actualCount > count)
                throw new InvalidOperationException("HID value capabilities could not be decoded.");
            var result = new HidpValueCaps[checked((int)actualCount)];
            for (var index = 0; index < result.Length; index++)
                result[index] = Marshal.PtrToStructure<HidpValueCaps>(IntPtr.Add(buffer, index * size));
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<TouchpadContactCollection> BuildContactCollections(
        IReadOnlyList<HidpValueCaps> valueCaps)
    {
        var result = new List<TouchpadContactCollection>();
        var linkCollections = valueCaps.Where(cap => cap.UsagePage == DigitizerUsagePage &&
                                                     cap.LinkCollection != 0 && cap.HasUsage(ContactIdUsage))
            .Select(cap => (cap.LinkCollection, cap.ReportId)).Distinct().OrderBy(cap => cap.LinkCollection)
            .ThenBy(cap => cap.ReportId).ToArray();
        foreach (var (linkCollection, reportId) in linkCollections)
        {
            var id = FindValueCap(valueCaps, ContactIdUsage, linkCollection, reportId, DigitizerUsagePage);
            var x = FindValueCap(valueCaps, XUsage, linkCollection, reportId, 0x01) ??
                    FindValueCap(valueCaps, XUsage, linkCollection, reportId, DigitizerUsagePage);
            var y = FindValueCap(valueCaps, YUsage, linkCollection, reportId, 0x01) ??
                    FindValueCap(valueCaps, YUsage, linkCollection, reportId, DigitizerUsagePage);
            if (id is null || x is null || y is null || x.Value.LogicalMax <= x.Value.LogicalMin ||
                y.Value.LogicalMax <= y.Value.LogicalMin) continue;
            result.Add(new TouchpadContactCollection(linkCollection, reportId, id.Value.ToRange(ContactIdUsage),
                x.Value.ToRange(XUsage), y.Value.ToRange(YUsage)));
        }

        return result;
    }

    private static HidpValueCaps? FindValueCap(IReadOnlyList<HidpValueCaps> valueCaps, ushort usage,
        ushort linkCollection, byte reportId, ushort? usagePage = null) => valueCaps.FirstOrDefault(cap =>
        (usagePage is null || cap.UsagePage == usagePage) && cap.ReportId == reportId &&
        cap.LinkCollection == linkCollection && cap.HasUsage(usage));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetCaps(nint preparsedData, out HidpCaps caps);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetValueCaps(HidpReportType reportType, nint valueCaps, ref uint valueCapsLength,
        nint preparsedData);

    private enum HidpReportType
    {
        Input,
        Output,
        Feature
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public ushort Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportId;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;

        public bool HasUsage(ushort usage) => IsRange != 0
            ? usage >= UsageMin && usage <= UsageMax
            : usage == UsageMin;

        public TouchpadValueRange ToRange(ushort usage) =>
            new(UsagePage, usage, LinkCollection, LogicalMin, LogicalMax);
    }
}

internal sealed class WindowsHidReportValueReader : IHidReportValueReader
{
    private const uint HidPStatusSuccess = 0x00110000;

    public unsafe bool TryGetValue(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
        ushort usage, out uint value)
    {
        fixed (byte* reportBytes = report)
            return HidP_GetUsageValue((int)HidpReportType.Input, usagePage, linkCollection, usage, out value,
                preparsedData, (nint)reportBytes, (uint)report.Length) == HidPStatusSuccess;
    }

    public unsafe bool TryGetUsages(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
        out IReadOnlyList<ushort> usages)
    {
        var nativeUsages = new ushort[64];
        var usageLength = (uint)nativeUsages.Length;
        bool succeeded;
        fixed (byte* reportBytes = report)
        fixed (ushort* usageList = nativeUsages)
        {
            succeeded = HidP_GetUsages((int)HidpReportType.Input, usagePage, linkCollection, usageList,
                ref usageLength, preparsedData, (nint)reportBytes, (uint)report.Length) == HidPStatusSuccess;
        }

        if (!succeeded || usageLength > nativeUsages.Length)
        {
            usages = [];
            return false;
        }

        usages = nativeUsages.AsSpan(0, (int)usageLength).ToArray();
        return true;
    }

    [DllImport("hid.dll")]
    private static extern uint HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, nint preparsedData, nint report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern unsafe uint HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        ushort* usageList, ref uint usageLength, nint preparsedData, nint report, uint reportLength);

    private enum HidpReportType
    {
        Input,
        Output,
        Feature
    }
}