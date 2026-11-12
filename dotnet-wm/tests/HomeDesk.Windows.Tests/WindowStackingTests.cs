using HomeDesk.Core;
using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class WindowStackingTests
{
    private const uint ExpectedPositionFlags = 0x00004213;

    [Fact]
    public void CombinedMonitorOrderIsPlacedBehindForegroundAndSkipsForegroundTarget()
    {
        var foreground = new nint(10);
        var firstMonitorWindow = Guid.NewGuid();
        var focusedWindow = Guid.NewGuid();
        var secondMonitorWindow = Guid.NewGuid();
        var native = new FakeStackingNative(foreground);
        var handles = new Dictionary<Guid, nint>
        {
            [firstMonitorWindow] = 11,
            [focusedWindow] = foreground,
            [secondMonitorWindow] = 12
        };

        Apply([firstMonitorWindow, focusedWindow, secondMonitorWindow], native, handles);

        Assert.Collection(native.Calls,
            call => Assert.Equal((new nint(11), foreground, ExpectedPositionFlags), call),
            call => Assert.Equal((new nint(12), new nint(11), ExpectedPositionFlags), call));
    }

    [Fact]
    public void UnmanagedForegroundRemainsTheInsertionAnchor()
    {
        var unmanagedForeground = new nint(99);
        var target = Guid.NewGuid();
        var native = new FakeStackingNative(unmanagedForeground);

        Apply([target], native, new Dictionary<Guid, nint> { [target] = 21 });

        Assert.Equal((new nint(21), unmanagedForeground, ExpectedPositionFlags),
            Assert.Single(native.Calls));
    }

    [Fact]
    public void TopmostForegroundUsesNormalBandInsteadOfBecomingAnInsertionAnchor()
    {
        var topmostForeground = new nint(99);
        var foregroundTarget = Guid.NewGuid();
        var normalTarget = Guid.NewGuid();
        var native = new FakeStackingNative(topmostForeground, topmostForeground);

        Apply([foregroundTarget, normalTarget], native, new Dictionary<Guid, nint>
        {
            [foregroundTarget] = topmostForeground,
            [normalTarget] = 21
        });

        Assert.Equal((new nint(21), nint.Zero, ExpectedPositionFlags),
            Assert.Single(native.Calls));
    }

    [Fact]
    public void StaleAndFailedTargetsDoNotAdvanceTheInsertionAnchor()
    {
        var foreground = new nint(10);
        var first = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var last = Guid.NewGuid();
        var native = new FakeStackingNative(foreground) { Results = [true, false, true] };
        var failures = new List<NativeActionFailure>();
        var handles = new Dictionary<Guid, nint> { [first] = 11, [failed] = 12, [last] = 13 };

        Apply([first, stale, failed, last], native, handles, failures);

        Assert.Collection(native.Calls,
            call => Assert.Equal((new nint(11), foreground, ExpectedPositionFlags), call),
            call => Assert.Equal((new nint(12), new nint(11), ExpectedPositionFlags), call),
            call => Assert.Equal((new nint(13), new nint(11), ExpectedPositionFlags), call));
        Assert.Collection(failures,
            failure => Assert.Equal((stale, "stale_window"), (failure.WindowId, failure.Code)),
            failure => Assert.Equal((failed, "native_error"), (failure.WindowId, failure.Code)));
    }

    private static void Apply(IReadOnlyList<Guid> windowIds, FakeStackingNative native,
        IReadOnlyDictionary<Guid, nint> handles, List<NativeActionFailure>? failures = null)
    {
        failures ??= [];
        WindowStacking.Apply(windowIds, native, _ => false,
            id => handles.TryGetValue(id, out var handle)
                ? new WindowStackingTarget(handle, null)
                : new WindowStackingTarget(default, new NativeActionFailure(id, "stale_window", "Stale.")),
            failures.Add, id => new NativeActionFailure(id, "native_error", "SetWindowPos failed."),
            CancellationToken.None);
    }

    private sealed class FakeStackingNative(nint foreground, nint topmost = default) : IWindowStackingNative
    {
        public nint ForegroundWindow { get; } = foreground;
        public List<(nint Window, nint InsertAfter, uint Flags)> Calls { get; } = [];
        public bool[] Results { get; init; } = [];

        public bool IsTopmost(nint window) => window == topmost;

        public bool SetWindowPos(nint window, nint insertAfter, uint flags)
        {
            Calls.Add((window, insertAfter, flags));
            return Calls.Count > Results.Length || Results[Calls.Count - 1];
        }
    }
}