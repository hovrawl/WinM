namespace HomeDeskManager;

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

internal sealed class CarouselScrollMotion
{
    private const double InputAcceleration = 3.2;
    private const double Friction = 3.5;
    private const double MaximumVelocity = 4600;
    private const double MinimumVelocity = 5;
    private double velocity;

    public double Velocity => velocity;
    public bool IsActive => velocity != 0;

    public void AddInput(double delta)
    {
        if (!double.IsFinite(delta) || delta == 0) return;
        velocity = Math.Clamp(velocity + delta * InputAcceleration, -MaximumVelocity, MaximumVelocity);
    }

    public double Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 || !IsActive) return 0;
        elapsedSeconds = Math.Min(elapsedSeconds, 0.1);
        var decay = Math.Exp(-Friction * elapsedSeconds);
        var displacement = velocity * (1 - decay) / Friction;
        velocity *= decay;
        if (Math.Abs(velocity) < MinimumVelocity) velocity = 0;
        return displacement;
    }

    public void Stop() => velocity = 0;
}