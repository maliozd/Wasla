using System.Diagnostics;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The headless-browser driver's cleanup with the real browser (WAS-73): disposal stops every process the browser
/// started, not only the browser process, and deletes its profile; a setup failure after start disposes the browser;
/// and disposing one browser leaves another browser, an Edge started outside the driver and a profile folder it did not
/// create untouched. Processes are observed through handles opened while the browser still tracks them.
/// </summary>
[Trait("Category", "Browser")]
public sealed class HeadlessChromiumCleanupBrowserTests
{
    [Fact]
    public async Task Dispose_StopsEveryProcessTheBrowserStarted_AndDeletesItsProfile()
    {
        var executable = BrowserOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var browser = await HeadlessChromium.StartAsync(executable, ct);
        var watches = new List<BrowserProcessTree.ProcessWatch>();
        var profile = browser.ProfilePath;
        try
        {
            Assert.Equal(2, await browser.EvaluateAsync<int>("1 + 1", ct));
            var tracked = browser.TrackedProcesses();
            // The browser process and the children it starts at once (crash handler, GPU, network, renderer).
            Assert.True(tracked.Count >= 2, $"Only {tracked.Count} browser process was tracked.");
            watches.AddRange(tracked.Select(BrowserProcessTree.Watch));
            Assert.True(File.Exists(Path.Combine(profile, DevToolsActivePort.FileName)));
        }
        finally
        {
            await browser.DisposeAsync();
        }

        AssertExited(watches);
        Assert.False(Directory.Exists(profile), "The browser profile directory was not deleted.");
    }

    [Fact]
    public async Task SetUp_ThatFailsAfterStart_DisposesTheBrowser_AndALaterDisposeIsSafe()
    {
        var executable = BrowserOrSkip();
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await HeadlessChromium.StartAsync(executable, ct);
        var watches = browser.TrackedProcesses().Select(BrowserProcessTree.Watch).ToList();
        var profile = browser.ProfilePath;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => browser.SetUpAsync(
            started => started.EvaluateAsync<bool>("(() => { throw new Error('setup failed'); })()", ct)));

        Assert.StartsWith("Script failed", error.Message);
        AssertExited(watches);
        Assert.False(Directory.Exists(profile), "The browser profile directory was not deleted.");
    }

    [Fact]
    public async Task TwoBrowsers_DisposingOne_LeavesTheOther_AnUnrelatedEdge_AndAnotherProfileFolder_Alone()
    {
        var executable = BrowserOrSkip();
        var ct = TestContext.Current.CancellationToken;
        // An Edge started outside the driver, with a profile folder named like the driver's: neither belongs to the
        // browsers below, so no cleanup of theirs may touch them.
        var unrelatedProfile = BrowserProfile.Create();
        var unrelated = Process.Start(UnrelatedEdge(executable, unrelatedProfile.Path))!;
        var unrelatedProcesses = BrowserProcessTree.Track(unrelated, Path.GetFileName(executable));
        unrelated.BeginErrorReadLine();
        unrelated.BeginOutputReadLine();
        HeadlessChromium? first = null;
        HeadlessChromium? second = null;
        var firstWatches = new List<BrowserProcessTree.ProcessWatch>();
        try
        {
            var starts = new[] { HeadlessChromium.StartAsync(executable, ct), HeadlessChromium.StartAsync(executable, ct) };
            try
            {
                await Task.WhenAll(starts);
            }
            finally
            {
                // A browser that did start is disposed below even when the other one failed to.
                first = starts[0].IsCompletedSuccessfully ? await starts[0] : null;
                second = starts[1].IsCompletedSuccessfully ? await starts[1] : null;
            }

            var firstIds = first!.TrackedProcesses().Select(p => p.Id).ToHashSet();
            var secondIds = second!.TrackedProcesses().Select(p => p.Id).ToHashSet();
            var unrelatedIds = unrelatedProcesses.Refresh().Select(p => p.Id).ToHashSet();
            Assert.Empty(firstIds.Intersect(secondIds));
            Assert.Empty(firstIds.Intersect(unrelatedIds));
            firstWatches.AddRange(first.TrackedProcesses().Select(BrowserProcessTree.Watch));
            using var secondBrowser = BrowserProcessTree.Watch(second.TrackedProcesses()[0]);
            using var unrelatedBrowser = BrowserProcessTree.Watch(unrelatedProcesses.Refresh()[0]);
            var firstProfile = first.ProfilePath;

            await first.DisposeAsync();

            AssertExited(firstWatches);
            Assert.False(Directory.Exists(firstProfile), "The disposed browser's profile was not deleted.");
            Assert.False(secondBrowser.HasExited, "The other browser was terminated.");
            Assert.True(Directory.Exists(second.ProfilePath), "The other browser's profile was deleted.");
            // Still answering: the cleanup of the first browser did not touch it.
            Assert.Equal(2, await second.EvaluateAsync<int>("1 + 1", ct));
            Assert.False(unrelatedBrowser.HasExited, "An Edge started outside the driver was terminated.");
            Assert.True(Directory.Exists(unrelatedProfile.Path), "A profile folder the browser did not create was deleted.");
        }
        finally
        {
            foreach (var watch in firstWatches)
                watch.Dispose();
            // Every browser is released even when an earlier cleanup fails; the unrelated Edge is this test's own and is
            // stopped by the same bounded cleanup.
            await DisposeAllAsync(
                () => first?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                () => second?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                () => new HeadlessBrowserCleanup(unrelatedProcesses, unrelatedProfile).RunAsync());
        }
    }

    private static async Task DisposeAllAsync(params Func<Task>[] disposals)
    {
        var failures = new List<Exception>();
        foreach (var dispose in disposals)
        {
            try { await dispose(); }
            catch (HeadlessBrowserCleanupException ex) { failures.Add(ex); }
        }

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    private static string BrowserOrSkip()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Process handles that outlive the browser's cleanup are observed through Windows APIs.");
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser cleanup checks.");
        return executable;
    }

    private static ProcessStartInfo UnrelatedEdge(string executable, string profile)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", $"--user-data-dir={profile}", "about:blank" })
            start.ArgumentList.Add(argument);
        start.Environment.Remove(HeadlessChromium.CompatibilityLayerVariable);
        return start;
    }

    private static void AssertExited(List<BrowserProcessTree.ProcessWatch> watches)
    {
        try
        {
            Assert.NotEmpty(watches);
            // Disposal waits until no process runs any more; Windows may finish tearing one down afterwards.
            Assert.All(watches, watch => Assert.True(watch.HasStopped, $"{watch} is still running after the browser was disposed."));
        }
        finally
        {
            foreach (var watch in watches)
                watch.Dispose();
        }
    }
}
