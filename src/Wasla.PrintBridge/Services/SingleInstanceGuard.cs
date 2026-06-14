namespace Wasla.PrintBridge.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Global\WaslaPrintBridge";

    private readonly Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceGuard? TryAcquire()
    {
        var createdNew = false;
        Mutex? mutex = null;

        try
        {
            mutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    public void Dispose()
    {
        if (!_ownsMutex || _mutex is null)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            _mutex.Dispose();
            _ownsMutex = false;
        }
    }
}
