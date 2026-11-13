using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-92: the startup decision of the app. Every test uses mutex names of its own, never the installed client's, and each
/// acquisition runs on its own thread, as a separate launch would. WAS-61: the app takes only this user's instance lock;
/// the machine-wide engine lock is the service's (<see cref="Service.PrintBridgeEngineLockTests"/>).
/// Same-account only: a lock held by another Windows account is covered by <see cref="SetupInstanceCrossAccountTests"/>.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void FirstLaunch_TakesTheInstanceLock_AndIsPrimary()
    {
        var names = LockNames.Create();

        var launch = Launch.Start(names);
        try
        {
            Assert.Equal(SingleInstanceOutcome.Primary, launch.Outcome);
            Assert.True(Exists(names.Instance));
        }
        finally
        {
            launch.Exit();
        }

        Assert.False(Exists(names.Instance));
    }

    [Fact]
    public void SecondLaunchOfTheSameUser_FindsItsInstance()
    {
        var names = LockNames.Create();
        var primary = Launch.Start(names);
        try
        {
            var second = Launch.Start(names);
            second.Exit();

            Assert.Equal(SingleInstanceOutcome.InstanceRunningForThisUser, second.Outcome);
            Assert.False(second.HeldGuard);
        }
        finally
        {
            primary.Exit();
        }
    }

    [Fact]
    public void InstanceLockThatCannotBeOpened_IsTreatedAsRunning()
    {
        var names = LockNames.Create();
        using var denied = MutexAcl.Create(initiallyOwned: false, names.Instance, out var created, DenyEveryone());
        Assert.True(created);
        AssertCannotBeOpened(names.Instance);

        var launch = Launch.Start(names);
        launch.Exit();

        Assert.Equal(SingleInstanceOutcome.InstanceRunningForThisUser, launch.Outcome);
    }

    [Fact]
    public void AnotherKindOfObjectHoldingTheLockName_IsNeverTreatedAsFree()
    {
        var instanceNames = LockNames.Create();
        using (new Semaphore(1, 1, instanceNames.Instance))
        {
            var launch = Launch.Start(instanceNames);
            launch.Exit();
            Assert.Equal(SingleInstanceOutcome.InstanceRunningForThisUser, launch.Outcome);
        }
    }

    [Fact]
    public void AfterThePrimaryExits_TheNextLaunchIsPrimary()
    {
        var names = LockNames.Create();
        Launch.Start(names).Exit();

        var next = Launch.Start(names);
        next.Exit();

        Assert.Equal(SingleInstanceOutcome.Primary, next.Outcome);
    }

    [Fact]
    public void LaunchesRacingEachOther_YieldExactlyOnePrimary()
    {
        const int launches = 8;
        var names = LockNames.Create();
        using var allTried = new Barrier(launches);
        var outcomes = new SingleInstanceOutcome[launches];
        var threads = Enumerable.Range(0, launches).Select(i => new Thread(() =>
        {
            allTried.SignalAndWait(TimeSpan.FromSeconds(10));
            using var guard = SingleInstanceGuard.TryAcquire(names.Instance, out outcomes[i]);
            // Hold the result until every launch has decided, as running instances would.
            allTried.SignalAndWait(TimeSpan.FromSeconds(10));
        })).ToArray();

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(1, outcomes.Count(o => o == SingleInstanceOutcome.Primary));
        Assert.Equal(launches - 1, outcomes.Count(o => o == SingleInstanceOutcome.InstanceRunningForThisUser));
    }

    [Fact]
    public void Scope_IsThisProcessUserAndSession()
    {
        var scope = PrintBridgeInstanceScope.Current();

        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        Assert.Equal(identity.User, scope.User);
        Assert.Equal(process.SessionId, scope.SessionId);
        Assert.Equal($@"Local\WaslaPrintBridge.Instance.{identity.User!.Value}", scope.InstanceMutexName);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"WaslaPrintBridge.Setup.v1.{identity.User.Value}.{process.SessionId}"),
            scope.SetupPipeName);
    }

    [Fact]
    public void Scope_GivesEachUserAndSessionItsOwnNames()
    {
        var alice = new SecurityIdentifier("S-1-5-21-1000000001-1000000002-1000000003-1001");
        var bob = new SecurityIdentifier("S-1-5-21-1000000001-1000000002-1000000003-1002");

        var aliceSession1 = new PrintBridgeInstanceScope(alice, 1);
        var aliceSession2 = new PrintBridgeInstanceScope(alice, 2);
        var bobSession1 = new PrintBridgeInstanceScope(bob, 1);

        Assert.NotEqual(aliceSession1.SetupPipeName, bobSession1.SetupPipeName);
        Assert.NotEqual(aliceSession1.SetupPipeName, aliceSession2.SetupPipeName);
        Assert.NotEqual(aliceSession1.InstanceMutexName, bobSession1.InstanceMutexName);
        // The mutex is in the session's own namespace; the pipe namespace is machine-wide, so it names the session.
        Assert.StartsWith(@"Local\", aliceSession1.InstanceMutexName);
        Assert.StartsWith(SetupInstanceChannel.PipeNamePrefix + ".", aliceSession1.SetupPipeName);
        Assert.DoesNotContain('\\', aliceSession1.SetupPipeName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrintBridgeInstanceScope(alice, -1));
    }

    private static bool Exists(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    private static MutexSecurity DenyEveryone()
    {
        // Nobody, including this user, may open the mutex; only the handle returned at creation works.
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl,
            AccessControlType.Deny));
        return security;
    }

    private static void AssertCannotBeOpened(string name) =>
        Assert.Throws<UnauthorizedAccessException>(() => Mutex.OpenExisting(name).Dispose());

    private sealed record LockNames(string Instance)
    {
        public static LockNames Create() => new($@"Local\WaslaPbTest.Instance.{Guid.NewGuid():N}");
    }

    /// <summary>One launch: acquires on a thread of its own and keeps the result until <see cref="Exit"/>.</summary>
    private sealed class Launch
    {
        private readonly ManualResetEventSlim _decided = new();
        private readonly ManualResetEventSlim _exit = new();
        private readonly Thread _thread;

        private Launch(LockNames names)
        {
            _thread = new Thread(() =>
            {
                var guard = SingleInstanceGuard.TryAcquire(names.Instance, out var outcome);
                Outcome = outcome;
                HeldGuard = guard is not null;
                _decided.Set();
                _exit.Wait(TimeSpan.FromSeconds(30));
                guard?.Dispose();
            });
        }

        public SingleInstanceOutcome Outcome { get; private set; }

        public bool HeldGuard { get; private set; }

        public static Launch Start(LockNames names)
        {
            var launch = new Launch(names);
            launch._thread.Start();
            Assert.True(launch._decided.Wait(TimeSpan.FromSeconds(10)));
            return launch;
        }

        public void Exit()
        {
            _exit.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)));
        }
    }
}
