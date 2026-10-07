using System.Diagnostics;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The headless-browser driver's startup without a browser: reading <c>DevToolsActivePort</c> while Edge is still
/// writing it (WAS-71), browser exit, timeout, cancellation, and cleanup after a failed start. The read, the process
/// state and the clock are scripted; the cleanup tests start a stand-in process instead of a browser.
/// </summary>
public sealed class HeadlessChromiumStartupTests
{
    private const string BrowserPath = "/devtools/browser/0b5c6e7e-9d55-4c43-8a5f-6f4d1c1c2a91";
    private const string Valid = "53017\n" + BrowserPath;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ValidFile_IsReturnedOnTheFirstRead_WithoutAnyDelay()
    {
        var file = new ScriptedPortFile(() => Valid);
        var clock = new FakeClock();

        var endpoint = await WaitAsync(file, clock);

        Assert.Equal(new DevToolsActivePort(53017, BrowserPath), endpoint);
        Assert.Equal(1, file.Reads);
        Assert.Empty(clock.Delays);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))] // ERROR_SHARING_VIOLATION, the CI failure
    [InlineData(unchecked((int)0x80070021))] // ERROR_LOCK_VIOLATION
    public async Task LockedFile_IsReadAgain_UntilTheBrowserReleasesIt(int hresult)
    {
        var file = new ScriptedPortFile(() => throw Locked(hresult), () => throw Locked(hresult), () => Valid);
        var clock = new FakeClock();

        var endpoint = await WaitAsync(file, clock);

        Assert.Equal(53017, endpoint.Port);
        Assert.Equal(3, file.Reads);
        Assert.Equal([DevToolsActivePort.RetryInterval, DevToolsActivePort.RetryInterval], clock.Delays);
    }

    [Fact]
    public async Task EmptyFile_IsReadAgain_UntilItIsComplete()
    {
        var file = new ScriptedPortFile(() => "", () => Valid);
        var clock = new FakeClock();

        var endpoint = await WaitAsync(file, clock);

        Assert.Equal(53017, endpoint.Port);
        Assert.Equal(2, file.Reads);
    }

    [Fact]
    public async Task AbsentFile_IsReadAgain_UntilTheBrowserCreatesIt()
    {
        var file = new ScriptedPortFile(() => throw new FileNotFoundException(), () => Valid);

        var endpoint = await WaitAsync(file, new FakeClock());

        Assert.Equal(53017, endpoint.Port);
        Assert.Equal(2, file.Reads);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("53017")]
    [InlineData("53017\n")]
    [InlineData("53017\n/devtools/bro")]
    [InlineData("53017\n/devtools/browser/0b5c6e7e-9d55")]
    public async Task PartlyWrittenFile_IsReadAgain_UntilItIsComplete(string partial)
    {
        var file = new ScriptedPortFile(() => partial, () => Valid);
        var clock = new FakeClock();

        var endpoint = await WaitAsync(file, clock);

        Assert.Equal(new DevToolsActivePort(53017, BrowserPath), endpoint);
        Assert.Equal(2, file.Reads);
        Assert.Single(clock.Delays);
    }

    [Theory]
    [InlineData("0\n" + BrowserPath)]
    [InlineData("65536\n" + BrowserPath)]
    [InlineData("-1\n" + BrowserPath)]
    [InlineData(" 53017\n" + BrowserPath)]
    [InlineData("port\n" + BrowserPath)]
    [InlineData("\n" + BrowserPath)]
    public void Parse_RejectsAnInvalidPort(string content)
    {
        Assert.False(DevToolsActivePort.TryParse(content, out var endpoint, out var problem));
        Assert.Null(endpoint);
        Assert.Equal("malformed (no valid port on its first line)", problem);
    }

    [Theory]
    [InlineData("53017")]
    [InlineData("53017\n")]
    [InlineData("53017\n/json/version")]
    [InlineData("53017\n/devtools/browser/")]
    [InlineData("53017\n/devtools/browser/not-a-guid")]
    [InlineData("53017\n/devtools/page/0b5c6e7e-9d55-4c43-8a5f-6f4d1c1c2a91")]
    public void Parse_RejectsAMissingOrIncompleteWebSocketPath(string content)
    {
        Assert.False(DevToolsActivePort.TryParse(content, out var endpoint, out var problem));
        Assert.Null(endpoint);
        Assert.Equal("malformed (no complete browser WebSocket path on its second line)", problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n")]
    public void Parse_ReportsAnEmptyFile(string? content)
    {
        Assert.False(DevToolsActivePort.TryParse(content, out _, out var problem));
        Assert.Equal("empty", problem);
    }

    [Theory]
    [InlineData("53017\n" + BrowserPath, 53017)]
    [InlineData("53017\r\n" + BrowserPath + "\r\n", 53017)]
    [InlineData("1\n" + BrowserPath, 1)]
    [InlineData("65535\n" + BrowserPath, 65535)]
    public void Parse_AcceptsACompleteFile(string content, int port)
    {
        Assert.True(DevToolsActivePort.TryParse(content, out var endpoint, out _));
        Assert.Equal(new DevToolsActivePort(port, BrowserPath), endpoint);
    }

    [Theory]
    [InlineData("absent", "absent")]
    [InlineData("locked", "locked by another process")]
    [InlineData("empty", "empty")]
    [InlineData("partial", "malformed (no complete browser WebSocket path on its second line)")]
    public async Task Timeout_EndsTheWaitAtTheStartupLimit_AndNamesTheFileState(string scenario, string state)
    {
        var file = new ScriptedPortFile(scenario switch
        {
            "absent" => () => throw new FileNotFoundException(),
            "locked" => () => throw Locked(unchecked((int)0x80070020)),
            "empty" => () => "",
            _ => () => "53017"
        });
        var clock = new FakeClock();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => WaitAsync(file, clock, timeout: TimeSpan.FromSeconds(1)));

        Assert.Equal($"The browser did not open a DevTools port within 1.0 s; after 1.0 s DevToolsActivePort was {state}.", error.Message);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Elapsed);
        Assert.Equal(21, file.Reads);
    }

    [Fact]
    public async Task Timeout_CountsTheTimeTheStartupHadAlreadyUsed()
    {
        var file = new ScriptedPortFile(() => throw new FileNotFoundException());
        var clock = new FakeClock { Elapsed = Timeout - TimeSpan.FromMilliseconds(100) };

        await Assert.ThrowsAsync<TimeoutException>(() => WaitAsync(file, clock));

        Assert.Equal(Timeout, clock.Elapsed);
        Assert.Equal(3, file.Reads);
    }

    [Fact]
    public async Task BrowserExit_EndsTheWaitAtOnce_WithItsExitCodeAndTheFileState()
    {
        var file = new ScriptedPortFile(() => throw new FileNotFoundException());
        var clock = new FakeClock();
        var checks = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DevToolsActivePort.WaitAsync(
            file.ReadAsync, () => ++checks < 3 ? null : "exited with code 3", () => clock.Elapsed, clock.DelayAsync,
            Timeout, TestContext.Current.CancellationToken));

        Assert.Equal("The browser exited with code 3 after 0.1 s before its DevTools port was usable; DevToolsActivePort was absent.", error.Message);
        Assert.Equal(3, file.Reads);
        Assert.Equal(2, clock.Delays.Count);
    }

    [Theory]
    [InlineData("access denied")]
    [InlineData("missing profile")]
    [InlineData("disk full")]
    public async Task PermanentReadFailure_PropagatesUnchanged_WithoutRetrying(string failure)
    {
        Exception permanent = failure switch
        {
            "access denied" => new UnauthorizedAccessException("Access to the path is denied."),
            "missing profile" => new DirectoryNotFoundException("Could not find a part of the path."),
            _ => new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))
        };
        var file = new ScriptedPortFile(() => throw permanent);
        var clock = new FakeClock();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => WaitAsync(file, clock));

        Assert.Same(permanent, error);
        Assert.Equal(1, file.Reads);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task Cancellation_InterruptsTheRetryDelay_AndStopsReading()
    {
        var file = new ScriptedPortFile(() => throw Locked(unchecked((int)0x80070020)));
        using var cancellation = new CancellationTokenSource();
        var delaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = DevToolsActivePort.WaitAsync(
            file.ReadAsync, () => null, () => TimeSpan.Zero,
            (_, token) =>
            {
                delaying.TrySetResult();
                return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            },
            Timeout, cancellation.Token);
        await delaying.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, file.Reads);
    }

    [Fact]
    public async Task AlreadyCancelledStartup_DoesNotReadTheFile()
    {
        var file = new ScriptedPortFile(() => Valid);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevToolsActivePort.WaitAsync(
            file.ReadAsync, () => null, () => TimeSpan.Zero, Task.Delay, Timeout, new CancellationToken(canceled: true)));

        Assert.Equal(0, file.Reads);
    }

    [Fact]
    public async Task RealFile_HeldExclusivelyAndPartlyWritten_IsReadOnceTheWriterCloses()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Sharing violations are Windows file-system behavior.");

        var directory = Directory.CreateTempSubdirectory("wasla-devtools-port-test-").FullName;
        FileStream? writer = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var path = Path.Combine(directory, DevToolsActivePort.FileName);
            writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var firstHalf = "53017\n/devtools/browser/0b5c6e7e"u8.ToArray();
            writer.Write(firstHalf);
            writer.Flush();

            var startup = Stopwatch.StartNew();
            var reads = 0;
            var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = DevToolsActivePort.WaitAsync(
                async token =>
                {
                    reads++;
                    try { return await DevToolsActivePort.ReadAsync(path, token); }
                    catch (IOException) { locked.TrySetResult(); throw; }
                },
                () => null, () => startup.Elapsed, Task.Delay, Timeout, cancellation.Token);

            await locked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(wait.IsCompleted);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(Valid[firstHalf.Length..]));
            await writer.DisposeAsync();

            var endpoint = await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(new DevToolsActivePort(53017, BrowserPath), endpoint);
            Assert.True(reads >= 2);
        }
        finally
        {
            cancellation.Cancel();
            writer?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)] // A clean exit is still a failure: the driver keeps the started process from handing off (WAS-83).
    public async Task Start_WhenTheBrowserExitsFirst_FailsWithoutWaitingForTheTimeout_AndDeletesTheProfile(int exitCode)
    {
        var standIn = new StandInBrowser(exitCode);
        var startup = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HeadlessChromium.StartAsync(
            "browser.exe", Timeout, standIn.Launch, TestContext.Current.CancellationToken));

        Assert.StartsWith($"The browser exited with code {exitCode} after ", error.Message);
        Assert.EndsWith(" before its DevTools port was usable; DevToolsActivePort was absent.", error.Message);
        Assert.True(startup.Elapsed < TimeSpan.FromSeconds(15), $"Startup took {startup.Elapsed}.");
        standIn.AssertCleanedUp();
    }

    [Fact]
    public async Task Start_OnTimeout_KillsTheProcessTree_AndDeletesTheProfile()
    {
        var standIn = new StandInBrowser(exitCode: null);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => HeadlessChromium.StartAsync(
            "browser.exe", TimeSpan.FromMilliseconds(500), standIn.Launch, TestContext.Current.CancellationToken));

        Assert.StartsWith("The browser did not open a DevTools port within 0.5 s; after ", error.Message);
        Assert.EndsWith(" DevToolsActivePort was absent.", error.Message);
        standIn.AssertCleanedUp();
    }

    [Fact]
    public async Task Start_OnCancellation_KillsTheProcessTree_AndDeletesTheProfile()
    {
        var standIn = new StandInBrowser(exitCode: null);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HeadlessChromium.StartAsync(
            "browser.exe", Timeout, standIn.Launch, cancellation.Token));

        standIn.AssertCleanedUp();
    }

    private static Task<DevToolsActivePort> WaitAsync(ScriptedPortFile file, FakeClock clock, TimeSpan? timeout = null) =>
        DevToolsActivePort.WaitAsync(
            file.ReadAsync, () => null, () => clock.Elapsed, clock.DelayAsync, timeout ?? Timeout,
            TestContext.Current.CancellationToken);

    private static IOException Locked(int hresult) =>
        new("The process cannot access the file because it is being used by another process.", hresult);

    /// <summary>Each read runs the next step; the last step repeats.</summary>
    private sealed class ScriptedPortFile(params Func<string>[] steps)
    {
        public int Reads { get; private set; }

        public Task<string> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[Math.Min(Reads, steps.Length - 1)];
            Reads++;
            return Task.FromResult(step());
        }
    }

    /// <summary>Virtual startup time: every delay is recorded and advances it at once.</summary>
    private sealed class FakeClock
    {
        public TimeSpan Elapsed { get; set; }
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(delay);
            Elapsed += delay;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Replaces the browser with a process that never writes a DevTools port: one that exits with
    /// <c>exitCode</c>, or one that keeps running (<c>exitCode</c> null). Records the profile the driver created
    /// and holds its own handle to the process, so cleanup can be checked after the driver disposed its copy.
    /// </summary>
    private sealed class StandInBrowser(int? exitCode)
    {
        private string? _profile;
        private Process? _watch;

        public Process? Launch(ProcessStartInfo browser)
        {
            _profile = browser.ArgumentList.Single(a => a.StartsWith("--user-data-dir=", StringComparison.Ordinal))["--user-data-dir=".Length..];
            Assert.True(Directory.Exists(_profile));

            var (file, arguments) = OperatingSystem.IsWindows()
                ? exitCode is { } code
                    ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", $"exit {code}" })
                    : (Path.Combine(Environment.SystemDirectory, "PING.EXE"), new[] { "-n", "120", "127.0.0.1" })
                : ("/bin/sh", new[] { "-c", exitCode is { } exit ? $"exit {exit}" : "sleep 120" });
            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = browser.RedirectStandardError,
                RedirectStandardOutput = browser.RedirectStandardOutput
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            var process = Process.Start(start)!;
            if (exitCode is null)
            {
                // A second handle, opened now, keeps reporting this process after the driver disposed its own.
                _watch = Process.GetProcessById(process.Id);
                _ = _watch.SafeHandle;
            }

            return process;
        }

        public void AssertCleanedUp()
        {
            if (_watch is not null)
                using (_watch)
                    Assert.True(_watch.HasExited, "The stand-in browser process is still running.");
            Assert.NotNull(_profile);
            Assert.False(Directory.Exists(_profile), "The browser profile directory was not deleted.");
        }
    }
}
