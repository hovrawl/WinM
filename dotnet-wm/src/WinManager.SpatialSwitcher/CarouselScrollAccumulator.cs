namespace WinManager.SpatialSwitcher;

internal sealed class CarouselScrollAccumulator(Action<Action> schedule, Action<double> apply)
{
    private readonly object gate = new();
    private double pendingDelta;
    private bool scheduled;

    public void Add(double delta)
    {
        if (!double.IsFinite(delta) || delta == 0) return;
        var scheduleDrain = false;
        lock (gate)
        {
            pendingDelta = Math.Clamp(pendingDelta + delta, -1000, 1000);
            if (!scheduled)
            {
                scheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain) schedule(Drain);
    }

    private void Drain()
    {
        double delta;
        lock (gate)
        {
            delta = pendingDelta;
            pendingDelta = 0;
            scheduled = false;
        }

        if (delta != 0) apply(delta);
    }
}