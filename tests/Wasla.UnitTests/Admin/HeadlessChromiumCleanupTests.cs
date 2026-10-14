using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The headless-browser driver's cleanup without a browser (WAS-73). Edge child processes can hold the profile after the
/// browser process exits, and <c>Process.Kill(entireProcessTree: true)</c> can throw while a child is already exiting;
/// both used to leave a partial <c>wasla-headless-*</c> profile behind, and a failed post-start setup left the whole
/// browser running. Process states, kill failures and the clock are scripted here; profile locks are real file handles,
/// and the ownership tests start stand-in processes instead of a browser.
/// </summary>
public sealed class HeadlessChromiumCleanupTests
{
    private const int AccessDenied = 5;

    [Fact]
    public async Task Cleanup_KillsTheTree_AndDeletesTheWholeProfile()
    {
        var profile = PopulatedProfile();
        var tree = new FakeTree(new FakeProcess(100), new FakeProcess(101), new FakeProcess(102));
        var clock = new FakeClock();

        await Cleanup(tree, profile, clock).RunAsync();

        Assert.Equal(1, tree.Kills);
        Assert.All(tree.Processes, p => Assert.True(p.HasExited));
        Assert.All(tree.Processes, p => Assert.Equal(0, p.Terminations));
        Assert.False(Directory.Exists(profile.Path));
        Assert.Equal(1, tree.Disposals);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task TreeKillThrows_ForAChildThatIsAlreadyExiting_IsNotAFailure_AndTheProfileIsDeleted()
    {
        var profile = PopulatedProfile();
        // The child has its exit code, but Windows has not finished tearing it down, and terminating it is refused.
        var exiting = new FakeProcess(101) { Stopped = true, OnTerminate = _ => new Win32Exception(AccessDenied) };
        var tree = new FakeTree(new FakeProcess(100), exiting)
        {
            OnKillTree = t =>
            {
                t.Processes[0].HasExited = true;
                throw new AggregateException("Not every browser process could be terminated.", new Win32Exception(AccessDenied));
            }
        };
        // Windows finishes tearing it down while the cleanup waits, before the profile is deleted.
        var clock = new FakeClock { OnDelay = _ => exiting.HasExited = true };

        await Cleanup(tree, profile, clock).RunAsync();

        Assert.True(exiting.HasExited);
        Assert.Equal(0, exiting.Terminations);
        Assert.Equal([HeadlessBrowserCleanup.PollInterval], clock.Delays);
        Assert.False(Directory.Exists(profile.Path));
    }

    [Fact]
    public async Task ATerminationRefusedOnce_ThenTheProcessExits_IsNotAFailure()
    {
        var profile = PopulatedProfile();
        var refusing = new FakeProcess(101) { OnTerminate = _ => new Win32Exception(AccessDenied) };
        var tree = new FakeTree(new FakeProcess(100), refusing)
        {
            OnKillTree = t =>
            {
                t.Processes[0].HasExited = true;
                throw new AggregateException("Not every browser process could be terminated.", new Win32Exception(AccessDenied));
            }
        };
        var clock = new FakeClock { OnDelay = _ => refusing.HasExited = true };

        await Cleanup(tree, profile, clock).RunAsync();

        Assert.True(refusing.HasExited);
        Assert.Equal(1, refusing.Terminations);
        Assert.Equal([HeadlessBrowserCleanup.PollInterval], clock.Delays);
        Assert.False(Directory.Exists(profile.Path));
    }

    [Fact]
    public async Task AStoppedChildStillHoldingTheProfile_FailsOnTheProfile_AndSaysTheProcessIsExiting()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("An open file blocks deleting its directory only on Windows.");
        var profile = PopulatedProfile();
        var locked = new FileStream(Path.Combine(profile.Path, "Default", "Cookies"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var tearingDown = new FakeProcess(101, "msedge.exe") { Stopped = true };
            var tree = new FakeTree(new FakeProcess(100, "msedge.exe"), tearingDown) { OnKillTree = t => t.Processes[0].HasExited = true };
            var clock = new FakeClock();

            var error = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(() => Cleanup(tree, profile, clock).RunAsync());

            Assert.StartsWith(
                $"Headless browser cleanup failed for profile {profile.Path}: the profile could not be deleted within 15.0 s.",
                error.Message);
            Assert.Contains("Process msedge.exe 101: stopped, but Windows had not finished tearing it down.", error.Message);
            // The cleanup waited for the teardown first, then for the profile: each wait is bounded on its own.
            Assert.Equal(HeadlessBrowserCleanup.ProcessExitTimeout + HeadlessBrowserCleanup.ProfileReleaseTimeout, clock.Elapsed);
        }
        finally
        {
            locked.Dispose();
            Directory.Delete(profile.Path, recursive: true);
        }
    }

    [Fact]
    public async Task TreeKillThrows_AndAChildStaysAlive_FailsAtTheDeadline_NamingEachProcess_AndStillDeletesTheProfile()
    {
        var profile = PopulatedProfile();
        var alive = new FakeProcess(101, "msedge.exe") { OnTerminate = _ => new Win32Exception(AccessDenied) };
        var tree = new FakeTree(new FakeProcess(100, "msedge.exe"), alive)
        {
            OnKillTree = t =>
            {
                t.Processes[0].HasExited = true;
                throw new AggregateException("Not every browser process could be terminated.", new Win32Exception(AccessDenied));
            }
        };
        var clock = new FakeClock();

        var error = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(() => Cleanup(tree, profile, clock).RunAsync());

        Assert.StartsWith(
            $"Headless browser cleanup failed for profile {profile.Path}: 1 of 2 browser processes still running 10.0 s after the kill "
            + "(the process-tree kill threw AggregateException).", error.Message);
        Assert.Contains("Process msedge.exe 100: exited after the kill.", error.Message);
        Assert.Contains($"Process msedge.exe 101: still running; terminating it failed: Win32Exception (Win32 error {AccessDenied})", error.Message);
        Assert.Contains("The process-tree kill threw AggregateException", error.Message);
        Assert.False(alive.HasExited);
        Assert.Equal(HeadlessBrowserCleanup.ProcessExitTimeout, clock.Elapsed);
        Assert.Equal(1, tree.Disposals);
        // A hundred checks of the tracked handles, but the whole machine's process list only about once a second.
        Assert.Equal(100, alive.Terminations);
        Assert.InRange(tree.Refreshes, 2, 12);
        // The profile is still deleted: nothing held it here.
        Assert.False(Directory.Exists(profile.Path));
    }

    [Fact]
    public async Task AChildTheTreeKillMissed_IsTerminatedOnItsOwn()
    {
        var profile = PopulatedProfile();
        var missed = new FakeProcess(102);
        var tree = new FakeTree(new FakeProcess(100), new FakeProcess(101), missed)
        {
            // A child the first pass did not stop, for example one created while the kill ran.
            OnKillTree = t =>
            {
                t.Processes[0].HasExited = true;
                t.Processes[1].HasExited = true;
            }
        };

        await Cleanup(tree, profile, new FakeClock()).RunAsync();

        Assert.True(missed.HasExited);
        Assert.Equal(1, missed.Terminations);
        Assert.False(Directory.Exists(profile.Path));
    }

    [Fact]
    public async Task AProcessThatHadAlreadyExited_IsNotTerminatedAgain()
    {
        var profile = PopulatedProfile();
        var crashed = new FakeProcess(100) { HasExited = true };
        var child = new FakeProcess(101);
        var tree = new FakeTree(crashed, child) { OnKillTree = _ => { } };

        await Cleanup(tree, profile, new FakeClock()).RunAsync();

        Assert.Equal(0, crashed.Terminations);
        Assert.Equal(1, child.Terminations);
        Assert.False(Directory.Exists(profile.Path));
    }

    [Fact]
    public async Task LockedProfileFile_IsDeleted_OnceItsHandleIsReleased()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("An open file blocks deleting its directory only on Windows.");
        var profile = PopulatedProfile();
        var locked = new FileStream(Path.Combine(profile.Path, "Default", "Cookies"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var clock = new FakeClock { OnDelay = count => { if (count == 3) locked.Dispose(); } };

            await Cleanup(new FakeTree(new FakeProcess(100)), profile, clock).RunAsync();

            Assert.False(Directory.Exists(profile.Path));
            Assert.Equal(3, clock.Delays.Count);
        }
        finally
        {
            locked.Dispose();
            if (Directory.Exists(profile.Path))
                Directory.Delete(profile.Path, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileStillLockedAtTheDeadline_FailsWithTheProfileState()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("An open file blocks deleting its directory only on Windows.");
        var profile = PopulatedProfile();
        var locked = new FileStream(Path.Combine(profile.Path, "Default", "Cookies"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var clock = new FakeClock();

            var error = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(
                () => Cleanup(new FakeTree(new FakeProcess(100, "msedge.exe")), profile, clock).RunAsync());

            Assert.StartsWith(
                $"Headless browser cleanup failed for profile {profile.Path}: the profile could not be deleted within 15.0 s.",
                error.Message);
            Assert.Contains("Process msedge.exe 100: exited after the kill.", error.Message);
            Assert.Contains("Last delete error: ", error.Message);
            Assert.Contains(Path.Combine("Default", "Cookies"), error.Message);
            Assert.Equal(HeadlessBrowserCleanup.ProfileReleaseTimeout, clock.Elapsed);
            Assert.True(File.Exists(locked.Name));
        }
        finally
        {
            locked.Dispose();
            Directory.Delete(profile.Path, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedAndConcurrentRuns_CleanUpOnce()
    {
        var profile = PopulatedProfile();
        var tree = new FakeTree(new FakeProcess(100));
        var cleanup = Cleanup(tree, profile, new FakeClock());

        await Task.WhenAll(cleanup.RunAsync(), cleanup.RunAsync());
        await cleanup.RunAsync();

        Assert.Equal(1, tree.Kills);
        Assert.Equal(1, tree.Disposals);
        Assert.False(Directory.Exists(profile.Path));
    }

    [Fact]
    public async Task AFailedCleanup_IsReportedAgain_ByEveryLaterRun()
    {
        var profile = PopulatedProfile();
        var stuck = new FakeProcess(100) { OnTerminate = _ => new Win32Exception(AccessDenied) };
        var tree = new FakeTree(stuck) { OnKillTree = _ => throw new Win32Exception(AccessDenied) };
        var cleanup = Cleanup(tree, profile, new FakeClock());

        var first = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(cleanup.RunAsync);
        var second = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(cleanup.RunAsync);

        Assert.Same(first, second);
        Assert.Equal(1, tree.Kills);
    }

    [Theory]
    [InlineData("the temporary folder")]
    [InlineData("the temporary folder with a separator")]
    [InlineData("the parent of the temporary folder")]
    [InlineData("the home folder")]
    [InlineData("the repository")]
    [InlineData("a prefixed folder outside the temporary folder")]
    [InlineData("a folder without the prefix")]
    [InlineData("the prefix alone")]
    [InlineData("a folder inside a profile")]
    [InlineData("a relative path")]
    [InlineData("a path that is not normalized")]
    public void ProfileValidation_RefusesEverythingButAPrefixedChildOfTheTemporaryFolder(string target)
    {
        var temp = Path.GetTempPath();
        var trimmed = Path.TrimEndingDirectorySeparator(temp);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = target switch
        {
            "the temporary folder" => trimmed,
            "the temporary folder with a separator" => temp,
            "the parent of the temporary folder" => Path.GetDirectoryName(trimmed)!,
            "the home folder" => home,
            "the repository" => RepositoryRoot(),
            "a prefixed folder outside the temporary folder" => Path.Combine(home, BrowserProfile.Prefix + "abc123"),
            "a folder without the prefix" => Path.Combine(trimmed, "wasla-other-abc123"),
            "the prefix alone" => Path.Combine(trimmed, BrowserProfile.Prefix),
            "a folder inside a profile" => Path.Combine(trimmed, BrowserProfile.Prefix + "abc123", "Default"),
            "a relative path" => BrowserProfile.Prefix + "abc123",
            _ => Path.Combine(trimmed, "x", "..", BrowserProfile.Prefix + "abc123")
        };

        Assert.NotNull(BrowserProfile.WhyNotDeletable(path, temp));
        Assert.Null(BrowserProfile.WhyNotDeletable(Path.Combine(trimmed, BrowserProfile.Prefix + "abc123"), temp));
    }

    [Fact]
    public async Task Cleanup_RefusesToDeleteADirectoryItDidNotCreate_AndSaysWhy()
    {
        var unrelated = Directory.CreateTempSubdirectory("wasla-cleanup-unrelated-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "not a browser profile");
            var clock = new FakeClock();

            var error = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(
                () => Cleanup(new FakeTree(new FakeProcess(100)), new BrowserProfile(unrelated, Path.GetTempPath()), clock).RunAsync());

            Assert.StartsWith(
                $"Headless browser cleanup failed for profile {unrelated}: the profile was not deleted because its name does not start with {BrowserProfile.Prefix}",
                error.Message);
            Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
            Assert.Empty(clock.Delays);
        }
        finally
        {
            Directory.Delete(unrelated, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_RefusesAProfileThatIsALink_AndLeavesItsTargetAlone()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Directory junctions are a Windows file-system feature.");
        var target = Directory.CreateTempSubdirectory("wasla-cleanup-target-").FullName;
        var junction = Path.Combine(Path.GetTempPath(), BrowserProfile.Prefix + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(target, "keep.txt"), "not a browser profile");
            using (var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                   {
                       ArgumentList = { "/d", "/c", "mklink", "/J", junction, target },
                       UseShellExecute = false,
                       CreateNoWindow = true,
                       RedirectStandardOutput = true
                   })!)
            {
                await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, mklink.ExitCode);
            }

            var error = await Assert.ThrowsAsync<HeadlessBrowserCleanupException>(
                () => Cleanup(new FakeTree(new FakeProcess(100)), new BrowserProfile(junction, Path.GetTempPath()), new FakeClock()).RunAsync());

            Assert.Contains("the profile was not deleted because it is a link to another location", error.Message);
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction); // removes the link only
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task SetUp_ThatFails_DisposesTheStartedBrowser_AndRethrowsTheFailure()
    {
        var standIn = StandIn.Start();
        var failure = new InvalidOperationException("Network.setCookie failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => standIn.Browser.SetUpAsync(async _ =>
        {
            await Task.Yield();
            throw failure;
        }));

        Assert.Same(failure, thrown);
        standIn.AssertCleanedUp();
    }

    [Fact]
    public async Task SetUp_Cancelled_DisposesTheStartedBrowser_BecauseCleanupIgnoresCancellation()
    {
        var standIn = StandIn.Start();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => standIn.Browser.SetUpAsync(_ => Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token)));

        Assert.True(cancellation.IsCancellationRequested);
        standIn.AssertCleanedUp();
    }

    [Fact]
    public async Task SetUp_ThatSucceeds_HandsTheBrowserOver_StillRunning()
    {
        var standIn = StandIn.Start();
        try
        {
            var browser = await standIn.Browser.SetUpAsync(_ => Task.CompletedTask);

            Assert.Same(standIn.Browser, browser);
            Assert.False(standIn.Watch.HasExited);
            Assert.True(Directory.Exists(standIn.Profile));
        }
        finally
        {
            await standIn.Browser.DisposeAsync();
        }

        standIn.AssertCleanedUp();
    }

    [Fact]
    public async Task Dispose_RepeatedAndConcurrent_IsSafe()
    {
        var standIn = StandIn.Start();

        await Task.WhenAll(standIn.Browser.DisposeAsync().AsTask(), standIn.Browser.DisposeAsync().AsTask());
        await standIn.Browser.DisposeAsync();

        standIn.AssertCleanedUp();
    }

    [Fact]
    public async Task TwoBrowsers_DisposingOne_LeavesTheOthersProcessesAndProfile_AndUnrelatedOnes_Alone()
    {
        var first = StandIn.Start();
        var second = StandIn.Start();
        // Not started by the driver: a process and a prefixed folder of another run must survive any cleanup.
        using var unrelated = StandIn.StartProcess();
        var leftover = Directory.CreateTempSubdirectory(BrowserProfile.Prefix).FullName;
        try
        {
            await first.Browser.DisposeAsync();

            first.AssertCleanedUp();
            Assert.False(second.Watch.HasExited, "The other browser's process was terminated.");
            Assert.True(Directory.Exists(second.Profile), "The other browser's profile was deleted.");
            Assert.False(unrelated.HasExited, "An unrelated process was terminated.");
            Assert.True(Directory.Exists(leftover), "An unrelated profile folder was deleted.");
        }
        finally
        {
            // This test's own unrelated process and folder go first, so a failing cleanup below cannot leave them.
            if (!unrelated.HasExited)
                unrelated.Kill();
            Directory.Delete(leftover, recursive: true);
            await second.Browser.DisposeAsync();
        }

        second.AssertCleanedUp();
    }

    private static HeadlessBrowserCleanup Cleanup(IBrowserProcessTree tree, BrowserProfile profile, FakeClock clock) =>
        new(tree, profile, () => clock.Elapsed, clock.DelayAsync);

    /// <summary>A real profile directory with the kind of content Edge leaves in it.</summary>
    private static BrowserProfile PopulatedProfile()
    {
        var profile = BrowserProfile.Create();
        Directory.CreateDirectory(Path.Combine(profile.Path, "Default", "Cache"));
        File.WriteAllText(Path.Combine(profile.Path, "Local State"), "{}");
        File.WriteAllText(Path.Combine(profile.Path, "Default", "Cookies"), "fake");
        File.WriteAllText(Path.Combine(profile.Path, "Default", "Cache", "data_0"), "fake");
        return profile;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Wasla.sln was not found above the test output.");
    }

    private sealed class FakeProcess(int id, string name = "msedge.exe") : IBrowserProcess
    {
        public int Id => id;

        public string Name => name;

        public bool HasExited { get; set; }

        /// <summary>Has an exit code while Windows is still tearing it down.</summary>
        public bool Stopped { get; set; }

        public bool HasStopped => HasExited || Stopped;

        public int Terminations { get; private set; }

        /// <summary>Returns the exception Terminate throws, or null after ending the process.</summary>
        public Func<FakeProcess, Exception?> OnTerminate { get; init; } = process =>
        {
            process.HasExited = true;
            return null;
        };

        public void Terminate()
        {
            Terminations++;
            if (OnTerminate(this) is { } error)
                throw error;
        }
    }

    private sealed class FakeTree(params FakeProcess[] processes) : IBrowserProcessTree
    {
        public List<FakeProcess> Processes { get; } = [.. processes];

        public int Kills { get; private set; }

        public int Refreshes { get; private set; }

        public int Disposals { get; private set; }

        public Action<FakeTree> OnKillTree { get; init; } = tree =>
        {
            foreach (var process in tree.Processes)
                process.HasExited = true;
        };

        public IReadOnlyList<IBrowserProcess> Refresh()
        {
            Refreshes++;
            return [.. Processes];
        }

        public void KillTree()
        {
            Kills++;
            OnKillTree(this);
        }

        public void Dispose() => Disposals++;
    }

    /// <summary>Virtual cleanup time: every delay is recorded and advances it at once.</summary>
    private sealed class FakeClock
    {
        public TimeSpan Elapsed { get; private set; }

        public List<TimeSpan> Delays { get; } = [];

        /// <summary>Runs after each delay with the number of delays so far.</summary>
        public Action<int>? OnDelay { get; init; }

        public Task DelayAsync(TimeSpan delay)
        {
            Delays.Add(delay);
            Elapsed += delay;
            OnDelay?.Invoke(Delays.Count);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A driver instance around a long-running stand-in process instead of a browser, with its own profile. A second
    /// handle to the process, opened at start, reports it after the driver has disposed its own.
    /// </summary>
    private sealed class StandIn
    {
        private StandIn(HeadlessChromium browser, Process watch, string profile)
        {
            Browser = browser;
            Watch = watch;
            Profile = profile;
        }

        public HeadlessChromium Browser { get; }

        public Process Watch { get; }

        public string Profile { get; }

        public static StandIn Start()
        {
            var profile = BrowserProfile.Create();
            var process = StartProcess();
            var watch = Process.GetProcessById(process.Id);
            _ = watch.SafeHandle;
            var browser = new HeadlessChromium(BrowserProcessTree.Track(process, "stand-in"), profile, new ClientWebSocket());
            return new StandIn(browser, watch, profile.Path);
        }

        public static Process StartProcess()
        {
            var (file, arguments) = OperatingSystem.IsWindows()
                ? (Path.Combine(Environment.SystemDirectory, "PING.EXE"), new[] { "-n", "120", "127.0.0.1" })
                : ("/bin/sh", new[] { "-c", "sleep 120" });
            var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            return Process.Start(start)!;
        }

        public void AssertCleanedUp()
        {
            using (Watch)
                Assert.True(Watch.HasExited, "The stand-in browser process is still running.");
            Assert.False(Directory.Exists(Profile), "The browser profile directory was not deleted.");
        }
    }
}
