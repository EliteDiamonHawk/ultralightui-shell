using System.Security.Principal;

namespace Chronos;

internal sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;

    public static SingleInstanceLease Acquire()
    {
        string sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { sid = Environment.UserName; }

        var mutex = new Mutex(false, $"Local\\KhaosShell-Chronos-{sid}", out bool created);
        if (!created)
        {
            mutex.Dispose();
            throw new InstanceAlreadyRunningException("Another KhaosShell controller is already running.");
        }

        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                throw new InstanceAlreadyRunningException("Another KhaosShell controller is already running.");
            }
            return new SingleInstanceLease(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

internal sealed class InstanceAlreadyRunningException : Exception
{
    public InstanceAlreadyRunningException(string message) : base(message) { }
}
