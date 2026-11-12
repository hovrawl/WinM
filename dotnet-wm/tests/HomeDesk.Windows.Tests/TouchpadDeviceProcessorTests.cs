using HomeDesk.Contracts;
using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class TouchpadDeviceProcessorTests
{
    [Fact]
    public void InterleavedTouchpadsKeepIndependentSwipeRecognitionState()
    {
        var firstDevice = new TouchpadDeviceProcessor(42);
        var secondDevice = new TouchpadDeviceProcessor(84);

        Process(firstDevice, 42, 100, 1, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(secondDevice, 84, 100, 1, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(firstDevice, 42, 140, 2, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(secondDevice, 84, 140, 2, 3, Contact(1, 0.3), Contact(2, 0.5), Contact(3, 0.7));
        Process(firstDevice, 42, 156, 3, 3, Contact(1, 0.1), Contact(2, 0.3), Contact(3, 0.5));
        Process(secondDevice, 84, 156, 3, 3, Contact(1, 0.5), Contact(2, 0.7), Contact(3, 0.9));

        var firstSwipe = Process(firstDevice, 42, 172, 4, 0);
        var secondSwipe = Process(secondDevice, 84, 172, 4, 0);

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(firstSwipe.Gestures).Direction);
        Assert.Equal(TouchpadSwipeDirection.Right, Assert.Single(secondSwipe.Gestures).Direction);
    }

    [Fact]
    public void HidTipLiftOffFramesFinalizeSwipeThroughTheDeviceProcessor()
    {
        var reader = new FakeHidReportValueReader();
        var collections = Enumerable.Range(1, 3).Select(collection => new TouchpadContactCollection(
            (ushort)collection, 1,
            new TouchpadValueRange(0x0d, 0x51, (ushort)collection, 0, 10),
            new TouchpadValueRange(0x01, 0x30, (ushort)collection, 0, 1000),
            new TouchpadValueRange(0x01, 0x31, (ushort)collection, 0, 1000))).ToArray();
        var decoder = new TouchpadHidDecoder(
            new TouchpadHidReportLayout(8, 1, collections,
                new TouchpadValueRange(0x0d, 0x54, 0, 0, 10),
                new TouchpadValueRange(0x0d, 0x56, 0, 0, 65535)),
            1, reader);
        var processor = new TouchpadDeviceProcessor(42, new TouchpadOptions());

        Assert.Empty(ProcessHidFrame(reader, decoder, processor, 0, [0.3, 0.5, 0.7], [true, true, true])
            .Gestures);
        Assert.Empty(ProcessHidFrame(reader, decoder, processor, 40, [0.3, 0.5, 0.7], [true, true, true])
            .Gestures);
        Assert.Empty(ProcessHidFrame(reader, decoder, processor, 56, [0.1, 0.3, 0.5], [true, true, true])
            .Gestures);

        var completion = ProcessHidFrame(reader, decoder, processor, 72, [0.1, 0.3, 0.5],
            [true, true, false]);

        Assert.Equal(TouchpadSwipeDirection.Left, Assert.Single(completion.Gestures).Direction);
    }

    private static TouchpadDeviceProcessingResult Process(TouchpadDeviceProcessor processor, nint deviceHandle,
        long timestamp, uint scanTime, int expectedContacts, params TouchpadContact[] contacts) =>
        processor.Process(new TouchpadContactReport(deviceHandle, timestamp, scanTime, expectedContacts, contacts));

    private static TouchpadContact Contact(uint id, double x) => new(id, true, x, 0.5);

    private static TouchpadDeviceProcessingResult ProcessHidFrame(FakeHidReportValueReader reader,
        TouchpadHidDecoder decoder, TouchpadDeviceProcessor processor, long timestamp, double[] xPositions,
        bool[] touching)
    {
        reader.Values[(0x0d, 0, 0x54)] = 3;
        reader.Values[(0x0d, 0, 0x56)] = (uint)timestamp;
        for (ushort index = 0; index < xPositions.Length; index++)
        {
            var collection = (ushort)(index + 1);
            reader.Values[(0x0d, collection, 0x51)] = (uint)(index + 1);
            reader.Values[(0x01, collection, 0x30)] = (uint)(xPositions[index] * 1000);
            reader.Values[(0x01, collection, 0x31)] = 500;
            reader.Usages[collection] = touching[index] ? [0x42] : [];
        }

        Assert.True(decoder.TryDecode(42, timestamp, [1, 0, 0, 0, 0, 0, 0, 0], out var report));
        return processor.Process(Assert.IsType<TouchpadContactReport>(report));
    }

    private sealed class FakeHidReportValueReader : IHidReportValueReader
    {
        public Dictionary<(ushort UsagePage, ushort LinkCollection, ushort Usage), uint> Values { get; } = [];
        public Dictionary<ushort, IReadOnlyList<ushort>> Usages { get; } = [];

        public bool TryGetValue(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
            ushort usage, out uint value) => Values.TryGetValue((usagePage, linkCollection, usage), out value);

        public bool TryGetUsages(nint preparsedData, byte[] report, ushort usagePage, ushort linkCollection,
            out IReadOnlyList<ushort> usages)
        {
            usages = Usages.GetValueOrDefault(linkCollection, []);
            return true;
        }
    }
}
