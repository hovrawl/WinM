using Xunit;

namespace WinManager.Windows.Tests;

public sealed class WindowIdentityMapTests
{
    [Fact]
    public void RepeatedIdentityIsStable()
    {
        var map = new WindowIdentityMap();
        var identity = new WindowIdentity(123, 4, 5);
        Assert.Equal(map.GetOrCreate(identity), map.GetOrCreate(identity));
    }

    [Theory]
    [InlineData(123, 6, 5)]
    [InlineData(123, 4, 6)]
    [InlineData(124, 4, 5)]
    public void HandlePidOrProcessCreationChangeCannotInheritIdentity(int handle, uint pid, ulong created)
    {
        var map = new WindowIdentityMap();
        var first = map.GetOrCreate(new WindowIdentity(123, 4, 5));
        Assert.NotEqual(first, map.GetOrCreate(new WindowIdentity(handle, pid, created)));
    }

    [Fact]
    public void LifecycleTombstonePreventsSameProcessHandleReuse()
    {
        var map = new WindowIdentityMap();
        var identity = new WindowIdentity(123, 4, 5);
        var first = map.GetOrCreate(identity);
        map.RecordLifecycleChange(123);
        map.RecordLifecycleChange(123);
        map.ReconcileLifecycle();
        var second = map.GetOrCreate(identity);
        Assert.NotEqual(first, second);
        map.ReconcileLifecycle();
        Assert.Equal(second, map.GetOrCreate(identity));
    }

    [Fact]
    public void DelayedLifecycleEventBeforeFirstObservationDoesNotReplaceNewIdentity()
    {
        var map = new WindowIdentityMap();
        var identity = new WindowIdentity(123, 4, 5);
        var first = map.GetOrCreate(identity, 1000);
        map.RecordLifecycleChange(123, 900);
        map.ReconcileLifecycle();
        Assert.Equal(first, map.GetOrCreate(identity, 1200));
        map.RecordLifecycleChange(123, 1100);
        map.ReconcileLifecycle();
        Assert.NotEqual(first, map.GetOrCreate(identity, 1200));
    }

    [Fact]
    public void LifecycleTimestampComparisonSurvivesTickCountWrap()
    {
        var map = new WindowIdentityMap();
        var identity = new WindowIdentity(123, 4, 5);
        var first = map.GetOrCreate(identity, uint.MaxValue - 5);
        var now = (ulong)uint.MaxValue + 11;
        map.RecordLifecycleChange(123, WindowIdentityMap.ExpandEventTime(5, now));
        map.ReconcileLifecycle();
        Assert.NotEqual(first, map.GetOrCreate(identity, now));
    }

    [Fact]
    public void MissingHandlesAreRetiredButHiddenEnumeratedHandlesAreRetained()
    {
        var map = new WindowIdentityMap();
        var identity = new WindowIdentity(123, 4, 5);
        var first = map.GetOrCreate(identity);
        map.Retain(new HashSet<nint> { 123 });
        Assert.Equal(first, map.GetOrCreate(identity));
        map.Retain(new HashSet<nint>());
        Assert.NotEqual(first, map.GetOrCreate(identity));
    }

    [Fact]
    public void LifecycleOverflowFailsClosedInsteadOfReusingUncertainIdentity()
    {
        var map = new WindowIdentityMap();
        var identity = new WindowIdentity(123, 4, 5);
        var first = map.GetOrCreate(identity);
        for (var i = 0; i < 5000; i++) map.RecordLifecycleChange(i + 1000);
        map.ReconcileLifecycle();
        Assert.NotEqual(first, map.GetOrCreate(identity));
    }
}