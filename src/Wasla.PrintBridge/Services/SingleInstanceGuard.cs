namespace Wasla.PrintBridge.Services;

/// <summary>How a launch relates to the Print Bridge app instances already running (WAS-92).</summary>
public enum SingleInstanceOutcome
{
    /// <summary>This process holds this user's instance lock in this session and receives forwarded setup links.</summary>
    Primary,

    /// <summary>
    /// This user's instance lock in this session already exists or cannot be opened. A setup link may only be forwarded to
    /// a pipe owned by this user.
    /// </summary>
    InstanceRunningForThisUser,
}

/// <summary>
/// The startup lock of the app: the instance lock of this Windows user in this session
/// (<see cref="PrintBridgeInstanceScope.InstanceMutexName"/>), scoped like the setup pipe, so a second launch finds its
/// own user's instance. WAS-61: the machine-wide engine lock moved to the service, which is the only engine; several
/// users' apps are clients of it at the same time.
/// A lock counts as acquired only when this process created it. A lock that exists, cannot be opened (another account's
/// access rules) or names another kind of object is never read as "no instance".
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _instance;

    private SingleInstanceGuard(Mutex instance)
    {
        _instance = instance;
    }

    /// <summary>Returns the guard only for <see cref="SingleInstanceOutcome.Primary"/>; dispose it on the same thread.</summary>
    public static SingleInstanceGuard? TryAcquire(out SingleInstanceOutcome outcome) =>
        TryAcquire(PrintBridgeInstanceScope.Current().InstanceMutexName, out outcome);

    internal static SingleInstanceGuard? TryAcquire(string instanceMutexName, out SingleInstanceOutcome outcome)
    {
        var instance = TryCreateOwned(instanceMutexName);
        if (instance is null)
        {
            outcome = SingleInstanceOutcome.InstanceRunningForThisUser;
            return null;
        }

        outcome = SingleInstanceOutcome.Primary;
        return new SingleInstanceGuard(instance);
    }

    private static Mutex? TryCreateOwned(string name)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Exists with access rules that exclude this user.
            return null;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Another kind of object (an event, a semaphore) holds the name.
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (createdNew)
            return mutex;

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        var instance = Interlocked.Exchange(ref _instance, null);
        if (instance is null)
            return;

        try
        {
            instance.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; disposing still closes the handle.
        }
        finally
        {
            instance.Dispose();
        }
    }
}
