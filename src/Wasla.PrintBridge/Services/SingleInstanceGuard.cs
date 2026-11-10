namespace Wasla.PrintBridge.Services;

/// <summary>How a launch relates to the Print Bridge instances already running (WAS-92).</summary>
public enum SingleInstanceOutcome
{
    /// <summary>This process holds both locks: it runs the engine and receives forwarded setup links.</summary>
    Primary,

    /// <summary>
    /// This user's instance lock in this session already exists or cannot be opened. No engine is started; a setup
    /// link may only be forwarded to a pipe owned by this user.
    /// </summary>
    InstanceRunningForThisUser,

    /// <summary>
    /// This user and session have no instance, but the machine-wide engine lock already exists or cannot be opened:
    /// another Windows user or session runs Print Bridge (or holds the name). No engine is started and nothing is
    /// forwarded, because no pipe of this user is listening.
    /// </summary>
    EngineRunningElsewhere,
}

/// <summary>
/// Startup locks of the desktop process. Two named mutexes, taken in this order:
/// <list type="number">
/// <item>The instance lock of this Windows user in this session (<see cref="PrintBridgeInstanceScope.InstanceMutexName"/>),
/// scoped like the setup pipe, so a second launch finds its own user's instance.</item>
/// <item>The machine-wide engine lock (<see cref="EngineMutexName"/>). Settings, the device token and print history
/// live in one ProgramData folder, so only one engine may poll and print on the machine, whichever user starts it.</item>
/// </list>
/// A lock counts as acquired only when this process created it. A lock that exists, cannot be opened (another
/// account's access rules) or names another kind of object is never read as "no instance": the process then starts no
/// engine.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string EngineMutexName = @"Global\WaslaPrintBridge";

    private Mutex? _instance;
    private Mutex? _engine;

    private SingleInstanceGuard(Mutex instance, Mutex engine)
    {
        _instance = instance;
        _engine = engine;
    }

    /// <summary>Returns the guard only for <see cref="SingleInstanceOutcome.Primary"/>; dispose it on the same thread.</summary>
    public static SingleInstanceGuard? TryAcquire(out SingleInstanceOutcome outcome) =>
        TryAcquire(PrintBridgeInstanceScope.Current().InstanceMutexName, EngineMutexName, out outcome);

    internal static SingleInstanceGuard? TryAcquire(string instanceMutexName, string engineMutexName, out SingleInstanceOutcome outcome)
    {
        var instance = TryCreateOwned(instanceMutexName);
        if (instance is null)
        {
            outcome = SingleInstanceOutcome.InstanceRunningForThisUser;
            return null;
        }

        var engine = TryCreateOwned(engineMutexName);
        if (engine is null)
        {
            Release(instance);
            outcome = SingleInstanceOutcome.EngineRunningElsewhere;
            return null;
        }

        outcome = SingleInstanceOutcome.Primary;
        return new SingleInstanceGuard(instance, engine);
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

    private static void Release(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; disposing still closes the handle.
        }
        finally
        {
            mutex.Dispose();
        }
    }

    public void Dispose()
    {
        // Reverse order of acquisition: the engine lock first, so a launch by this user that arrives meanwhile still
        // sees this user's instance rather than reporting another user.
        var engine = Interlocked.Exchange(ref _engine, null);
        if (engine is not null)
            Release(engine);

        var instance = Interlocked.Exchange(ref _instance, null);
        if (instance is not null)
            Release(instance);
    }
}
