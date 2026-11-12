namespace FMSC_SMS.Core;

/// <summary>One interactive FMSC process per computer, independent of install path or version.</summary>
internal sealed class AppInstanceLease : IDisposable
{
    internal const string Name = @"Global\FMSC_SMS.Interactive";
    private Mutex? gate;
    private AppInstanceLease(Mutex gate) => this.gate = gate;

    internal static AppInstanceLease? TryAcquire(string name = Name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool held;
            try { held = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; } // A crashed process must not block reopening.
            if (held) return new AppInstanceLease(mutex);
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }

    // Acquire and release on the startup STA thread; keep the lease through login and Application.Run.
    public void Dispose()
    {
        Mutex? mutex = gate;
        if (mutex is null) return;
        gate = null;
        try { mutex.ReleaseMutex(); }
        finally { mutex.Dispose(); }
    }
}
