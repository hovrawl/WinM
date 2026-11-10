namespace WinManager.Host;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly ManualResetEventSlim release = new();
    private readonly Thread owner;
    private int disposed;
    public bool Acquired { get; }

    public SingleInstanceLease(string name)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name);
                var acquired = false;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                ready.TrySetResult(acquired);
                if (!acquired) return;
                try { release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "WinManager single-instance owner" };
        owner.Start();
        Acquired = ready.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set();
        owner.Join();
        release.Dispose();
    }
}