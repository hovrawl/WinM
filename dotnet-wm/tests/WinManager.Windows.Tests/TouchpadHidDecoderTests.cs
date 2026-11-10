using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
        Assert.Equal((64, 72), RawHidTouchpadDevice.NativeStructureSizes);
    }

    [Theory]
    [InlineData(0xff00, 0x05, 0, 0, "unsupported_device")]
    [InlineData(0x0d, 0x04, 16, 8, "unsupported_device")]
    [InlineData(0x0d, 0x05, 0, 8, "unsupported_descriptor")]
    [InlineData(0x0d, 0x05, 16, 0, "unsupported_descriptor")]
    [InlineData(0x0d, 0x05, 16, 513, "unsupported_descriptor")]
    [InlineData(0x0d, 0x05, 16, 8, null)]
    public void CapabilityValidationDistinguishesUnrelatedCollectionsFromUnsupportedDescriptors(
        int usagePage, int usage, int reportLength, int valueCapsCount, string? expectedFailure)
    {
        var caps = new RawHidTouchpadDevice.HidpCaps
        {
            UsagePage = (ushort)usagePage,
            Usage = (ushort)usage,
            InputReportByteLength = (ushort)reportLength,
            NumberInputValueCaps = (ushort)valueCapsCount
        };

        Assert.Equal(expectedFailure, RawHidTouchpadDevice.ValidateCapabilities(caps));
    }

    [Fact]
    public void HidpCapsFixtureReadsInputValueCapabilityCountAtSdkOffset()
    {
        var fixture = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(48), 7);

        var caps = ReadNativeStructure<RawHidTouchpadDevice.HidpCaps>(fixture);

        Assert.Equal((ushort)7, caps.NumberInputValueCaps);
    }

    [Fact]
    public void HidpValueCapsFixtureReadsRangesAndUsesSdkArrayStride()
    {
        var fixture = new byte[72 * 2];
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(40), -10);
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(44), 4095);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(56), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(58), 0x31);
        fixture[12] = 1;

        var secondOffset = 72;
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(secondOffset + 40), 1);
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(secondOffset + 44), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(secondOffset + 56), 0x51);

        var range = ReadNativeStructure<RawHidTouchpadDevice.HidpValueCaps>(fixture);
        var single = ReadNativeStructure<RawHidTouchpadDevice.HidpValueCaps>(fixture, secondOffset);

        Assert.Equal(-10, range.LogicalMin);
        Assert.Equal(4095, range.LogicalMax);
        Assert.True(range.HasUsage(0x31));
        Assert.Equal(1, single.LogicalMin);
        Assert.Equal(10, single.LogicalMax);
        Assert.True(single.HasUsage(0x51));
    }

    private static T ReadNativeStructure<T>(byte[] fixture, int offset = 0) where T : struct
    {
        var buffer = Marshal.AllocHGlobal(fixture.Length);
        try
        {
            Marshal.Copy(fixture, 0, buffer, fixture.Length);
            return Marshal.PtrToStructure<T>(IntPtr.Add(buffer, offset));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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