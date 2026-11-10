using WinManager.Windows;
using Xunit;

namespace WinManager.Windows.Tests;

public sealed class TouchpadHidDecoderTests
{
    [Fact]
    public void DescriptorRangesDecodeAndNormalizeContactsByCollection()
    {
        var reader = new FakeHidReportValueReader();
        reader.Values[(0x0d, 0, 0x54)] = 1;
        reader.Values[(0x0d, 0, 0x56)] = 77;
        reader.Values[(0x0d, 1, 0x51)] = 4;
        reader.Values[(0x01, 1, 0x30)] = 500;
        reader.Values[(0x01, 1, 0x31)] = 250;
        reader.Usages[1] = [0x42];
        var decoder = Decoder(reader);

        Assert.True(decoder.TryDecode(11, 100, [1, 0, 0, 0, 0, 0, 0, 0], out var decoded));

        var report = Assert.IsType<TouchpadContactReport>(decoded);
        Assert.Equal(1, report.ExpectedContactCount);
        Assert.Equal((uint)77, report.ScanTime);
        var contact = Assert.Single(report.Contacts);
        Assert.Equal((uint)4, contact.Id);
        Assert.Equal(0.5, contact.X, 6);
        Assert.Equal(0.25, contact.Y, 6);
        Assert.Contains((UsagePage: (ushort)0x01, LinkCollection: (ushort)1, Usage: (ushort)0x30),
            reader.ValueQueries);
    }

    [Fact]
    public void UnrelatedReportIdsAreIgnoredAndMalformedValuesAreRejected()
    {
        var reader = new FakeHidReportValueReader();
        reader.Values[(0x0d, 0, 0x54)] = 1;
        reader.Values[(0x0d, 1, 0x51)] = 2;
        reader.Values[(0x01, 1, 0x30)] = 1001;
        reader.Values[(0x01, 1, 0x31)] = 500;
        reader.Usages[1] = [0x42];
        var decoder = Decoder(reader);

        Assert.True(decoder.TryDecode(11, 100, [2, 0, 0, 0, 0, 0, 0, 0], out var ignored));
        Assert.Null(ignored);
        Assert.False(decoder.TryDecode(11, 101, [1, 0, 0, 0, 0, 0, 0, 0], out var malformed));
        Assert.Null(malformed);
        Assert.False(decoder.TryDecode(11, 102, [1], out _));
    }

    [Fact]
    public void HidCapabilityStructuresMatchTheDocumentedNativeAbiSizes()
    {
        Assert.Equal((32, 64), RawHidTouchpadDevice.NativeStructureSizes);
    }

    private static TouchpadHidDecoder Decoder(FakeHidReportValueReader reader)
    {
        var contactId = new TouchpadValueRange(0x0d, 0x51, 1, 0, 10);
        var x = new TouchpadValueRange(0x01, 0x30, 1, 0, 1000);
        var y = new TouchpadValueRange(0x01, 0x31, 1, 0, 1000);
        var layout = new TouchpadHidReportLayout(8, 1,
            [new TouchpadContactCollection(1, 1, contactId, x, y)],
            new TouchpadValueRange(0x0d, 0x54, 0, 0, 10),
            new TouchpadValueRange(0x0d, 0x56, 0, 0, 65535));
        return new TouchpadHidDecoder(layout, 5, reader);
    }

    private sealed class FakeHidReportValueReader : IHidReportValueReader
    {
        public Dictionary<(ushort UsagePage, ushort LinkCollection, ushort Usage), uint> Values { get; } = [];
        public Dictionary<ushort, IReadOnlyList<ushort>> Usages { get; } = [];
        public List<(ushort UsagePage, ushort LinkCollection, ushort Usage)> ValueQueries { get; } = [];

        public bool TryGetValue(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
            ushort usage, out uint value)
        {
            ValueQueries.Add((usagePage, linkCollection, usage));
            return Values.TryGetValue((usagePage, linkCollection, usage), out value);
        }

        public bool TryGetUsages(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
            out IReadOnlyList<ushort> usages)
        {
            usages = Usages.GetValueOrDefault(linkCollection, []);
            return true;
        }
    }
}