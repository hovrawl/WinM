namespace WinManager.Windows;

internal readonly record struct WindowIdentity(nint Handle, uint ProcessId, ulong ProcessCreationTime);

internal sealed class WindowIdentityMap
{
    private const int MaximumPendingChanges = 4096;
    private readonly Dictionary<nint, (WindowIdentity Identity, Guid Id, ulong ObservedAt)> identities = [];
    private readonly Dictionary<nint, ulong> lifecycleChanges = [];
    private bool overflow;

    // Called only on the native thread; callbacks record tombstones, never inspect windows.
    public void RecordLifecycleChange(nint handle, ulong eventTime = 0)
    {
        if (overflow) return;
        if (lifecycleChanges.TryGetValue(handle, out var previous))
        {
            if (eventTime > previous) lifecycleChanges[handle] = eventTime;
            return;
        }
        if (lifecycleChanges.Count >= MaximumPendingChanges)
        {
            lifecycleChanges.Clear();
            overflow = true;
        }
        else lifecycleChanges.Add(handle, eventTime);
    }

    public void ReconcileLifecycle()
    {
        if (overflow) identities.Clear();
        else foreach (var (handle, eventTime) in lifecycleChanges)
        {
            // Out-of-context events can arrive after a snapshot of the newly created window.
            if (identities.TryGetValue(handle, out var entry) && eventTime >= entry.ObservedAt)
                identities.Remove(handle);
        }
        lifecycleChanges.Clear();
        overflow = false;
    }

    public Guid GetOrCreate(WindowIdentity identity, ulong observedAt = 0)
    {
        if (identities.TryGetValue(identity.Handle, out var existing) && existing.Identity == identity)
            return existing.Id;
        var id = Guid.NewGuid();
        identities[identity.Handle] = (identity, id, observedAt);
        return id;
    }

    public static ulong ExpandEventTime(uint eventTime, ulong now)
    {
        var age = unchecked((uint)now - eventTime);
        return now >= age ? now - age : 0;
    }

    public bool TryResolve(Guid id, out WindowIdentity identity)
    {
        ReconcileLifecycle();
        foreach (var entry in identities.Values)
        {
            if (entry.Id != id) continue;
            identity = entry.Identity;
            return true;
        }
        identity = default;
        return false;
    }

    public void Retain(ISet<nint> handles)
    {
        foreach (var handle in identities.Keys.Where(handle => !handles.Contains(handle)).ToArray())
            identities.Remove(handle);
    }
}