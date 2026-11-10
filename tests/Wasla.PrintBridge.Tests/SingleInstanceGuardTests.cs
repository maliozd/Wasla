using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-92: the startup decision of the desktop process. Every test uses mutex names of its own, never the installed
/// client's, and each acquisition runs on its own thread, as a separate launch would.
/// Same-account only: a lock held by another Windows account is covered by <see cref="SetupInstanceCrossAccountTests"/>.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void FirstLaunch_TakesBothLocks_AndIsPrimary()
    {
        var names = LockNames.Create();

        var launch = Launch.Start(names);
        try
        {
            Assert.Equal(SingleInstanceOutcome.Primary, launch.Outcome);
            Assert.True(Exists(names.Instance));
            Assert.True(Exists(names.Engine));
        }
        finally
        {
            launch.Exit();
        }

        Assert.False(Exists(names.Instance));
        Assert.False(Exists(names.Engine));
    }

    [Fact]
    public void SecondLaunchOfTheSameUser_FindsItsInstance_AndStartsNoEngine()
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
    public void EngineHeldByAnotherUserOrSession_IsRefused_AndReleasesThisUsersInstanceLock()
    {
        var names = LockNames.Create();

        // Another user's or session's primary holds the machine-wide engine lock; its instance lock is its own.
        using var otherEngine = new Mutex(initiallyOwned: false, names.Engine, out var created);
        Assert.True(created);

        var launch = Launch.Start(names);
        launch.Exit();

        Assert.Equal(SingleInstanceOutcome.EngineRunningElsewhere, launch.Outcome);
        Assert.False(launch.HeldGuard);
        // The refused launch does not keep this user's instance lock, so it cannot block this user's next launch.
        Assert.False(Exists(names.Instance));
    }

    [Fact]
    public void EngineLockThatCannotBeOpened_IsNeverTreatedAsFree()
    {
        var names = LockNames.Create();

        // Access rules that exclude this user, as on a lock another account created.
        using var denied = MutexAcl.Create(initiallyOwned: false, names.Engine, out var created, DenyEveryone());
        Assert.True(created);
        AssertCannotBeOpened(names.Engine);

        var launch = Launch.Start(names);
        launch.Exit();

        Assert.Equal(SingleInstanceOutcome.EngineRunningElsewhere, launch.Outcome);
        Assert.False(Exists(names.Instance));
    }

    [Fact]
    public void InstanceLockThatCannotBeOpened_IsTreatedAsRunning_AndTheEngineLockIsNotTaken()
    {
        var names = LockNames.Create();
        using var denied = MutexAcl.Create(initiallyOwned: false, names.Instance, out var created, DenyEveryone());
        Assert.True(created);
        AssertCannotBeOpened(names.Instance);

        var launch = Launch.Start(names);
        launch.Exit();

        Assert.Equal(SingleInstanceOutcome.InstanceRunningForThisUser, launch.Outcome);
        Assert.False(Exists(names.Engine));
    }

    [Fact]
    public void AnotherKindOfObjectHoldingALockName_IsNeverTreatedAsFree()
    {
        var engineNames = LockNames.Create();
        using (new EventWaitHandle(false, EventResetMode.ManualReset, engineNames.Engine))
        {
            var launch = Launch.Start(engineNames);
            launch.Exit();
            Assert.Equal(SingleInstanceOutcome.EngineRunningElsewhere, launch.Outcome);
        }

        var instanceNames = LockNames.Create();
        using (new Semaphore(1, 1, instanceNames.Instance))
        {
            var launch = Launch.Start(instanceNames);
            launch.Exit();
            Assert.Equal(SingleInstanceOutcome.InstanceRunningForThisUser, launch.Outcome);
            Assert.False(Exists(instanceNames.Engine));
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
    public void LaunchesRacingEachOther_YieldExactlyOnePrimary_AndNoOtherEngine()
    {
        const int launches = 8;
        var names = LockNames.Create();
        using var allTried = new Barrier(launches);
        var outcomes = new SingleInstanceOutcome[launches];
        var threads = Enumerable.Range(0, launches).Select(i => new Thread(() =>
        {
            allTried.SignalAndWait(TimeSpan.FromSeconds(10));
            using var guard = SingleInstanceGuard.TryAcquire(names.Instance, names.Engine, out outcomes[i]);
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
    public void TheEngineLockStaysMachineWide()
    {
        // One ProgramData folder holds settings, token and history for every user; one engine per machine.
        Assert.Equal(@"Global\WaslaPrintBridge", SingleInstanceGuard.EngineMutexName);
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

    [Fact]
    public void RunningInAnotherSessionMessage_IsTranslatedInEveryCulture()
    {
        var turkish = PrintBridgeLocalizer.GetStringForCulture("Message.RunningInAnotherSession", "tr-TR", "\n");
        foreach (var culture in SupportedCultures.All)
        {
            var text = PrintBridgeLocalizer.GetStringForCulture("Message.RunningInAnotherSession", culture, "\n");

            Assert.NotEqual("Message.RunningInAnotherSession", text);
            Assert.Contains("Print Bridge", text);
            Assert.Contains("Windows", text);
            Assert.Contains("\n\n", text);
            if (culture != "tr-TR")
                Assert.NotEqual(turkish, text);
        }
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

    private sealed record LockNames(string Instance, string Engine)
    {
        public static LockNames Create()
        {
            var id = Guid.NewGuid().ToString("N");
            return new LockNames($@"Local\WaslaPbTest.Instance.{id}", $@"Local\WaslaPbTest.Engine.{id}");
        }
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
                var guard = SingleInstanceGuard.TryAcquire(names.Instance, names.Engine, out var outcome);
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
