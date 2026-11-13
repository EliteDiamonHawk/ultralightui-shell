namespace UltraLightStartMenu;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;

    public static SingleInstanceLease Acquire()
    {
        Mutex mutex = new(false, StartMenuIpcNames.MutexName, out bool created);
        if (!created)
        {
            mutex.Dispose();
            throw new StartMenuInstanceAlreadyRunningException(
                "Another UltraLightStartMenu instance is already running.");
        }

        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                throw new StartMenuInstanceAlreadyRunningException(
                    "Another UltraLightStartMenu instance is already running.");
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

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

public sealed class StartMenuInstanceAlreadyRunningException : Exception
{
    public StartMenuInstanceAlreadyRunningException(string message) : base(message) { }
}
