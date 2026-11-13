using System.Security.AccessControl;
using System.Security.Principal;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// The machine-wide engine lock (WAS-61, the lock WAS-92 introduced): a named mutex that exists only while one engine runs.
/// It counts as taken only when this process created it. A mutex that already exists, cannot be opened, or names another
/// kind of object means another engine may be running (for example an older Print Bridge app with its own engine), so
/// this engine does not start. The service creates it with access for SYSTEM, administrators and itself only, so an
/// older app finds it held either way and starts no engine.
/// </summary>
internal sealed class PrintBridgeEngineLock : IDisposable
{
    private Mutex? _mutex;

    private PrintBridgeEngineLock(Mutex mutex) => _mutex = mutex;

    public static PrintBridgeEngineLock? TryAcquire(string name)
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (self.User is { } user)
                security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        }

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, name, out createdNew, security);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (createdNew)
            return new PrintBridgeEngineLock(mutex);

        mutex.Dispose();
        return null;
    }

    public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Dispose();
}
