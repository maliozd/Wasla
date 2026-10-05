using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-58: a setup link that would move Print Bridge from server A to server B while a print job is active. The real
/// engine and setup coordinator run against two fake servers with their own fake tokens and a printer that only records.
/// Every phase of the job is held with a gate while the link is opened, so each ordering is reached deterministically.
/// A job claimed from A must be reported only to A, and a refused link must change nothing.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeSetupLinkGuardTests : IDisposable
{
    private const string HostA = "server-a.test";
    private const string HostB = "server-b.test";
    private const string UrlA = "http://server-a.test";
    private const string UrlB = "http://server-b.test";
    private const string TokenA = "token-a-not-a-real-credential";
    private const string TokenB = "token-b-not-a-real-credential";
    private const string SetupCode = "setup-code-not-a-real-credential";
    private const string CompletionCredential = "completion-not-a-real-credential";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly PrintBridgeProtocolSetupRequest LinkToB = new(UrlB, SetupCode);

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-setup-guard-tests");

    public void Dispose() => _dataRoot.Dispose();

    [Fact]
    public async Task ALinkOpenedBeforeAnyClaim_IsApplied_AndTheJobOfferedByTheOldServerIsNeverClaimed()
    {
        var poll = new Gate();
        var servers = new FakeServers { HoldFirstPendingPollOfA = poll };
        var jobId = servers.AddJob(HostA);
        var printer = new RecordingPrinter();
        using var bridge = Bridge.Create(servers, printer);
        bridge.Runtime.Start();
        await poll.Entered.Task.WaitAsync(Wait, Ct);

        // No job is active yet: the link wins and B becomes the connection.
        var outcome = await bridge.ApplyLinkAsync();
        Assert.Equal(PrintBridgeAutoSetupOutcome.Connected, outcome);

        // The poll that was already on its way to A now answers with A's job; it must not be claimed anywhere.
        var pollOfB = servers.NextPoll(HostB);
        poll.Open();
        await pollOfB.WaitAsync(Wait, Ct);
        await servers.NextPoll(HostB).WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Empty(servers.ReportsFor(jobId));
        Assert.Equal(JobState.Pending, servers.State(jobId));
        Assert.Equal(0, printer.Copies);
        Assert.Equal((UrlB, TokenB), bridge.SavedConnection());
    }

    [Fact]
    public async Task ALinkOpenedWhileTheClaimIsInFlight_IsRefused_AndTheJobFinishesOnlyOnTheOldServer()
    {
        var claim = new Gate();
        var servers = new FakeServers { HoldClaim = claim };
        await RefusedWhileHeldThenAppliedAsync(servers, servers.AddJob(HostA), claim, new RecordingPrinter());
    }

    [Fact]
    public async Task ALinkOpenedAfterTheClaimBeforePrinting_IsRefused_AndTheJobFinishesOnlyOnTheOldServer()
    {
        var claimAnswer = new Gate();
        var servers = new FakeServers { HoldClaimAnswer = claimAnswer };
        await RefusedWhileHeldThenAppliedAsync(servers, servers.AddJob(HostA), claimAnswer, new RecordingPrinter());
    }

    [Fact]
    public async Task ALinkOpenedDuringPrinting_IsRefused_AndTheJobFinishesOnlyOnTheOldServer()
    {
        var print = new Gate();
        var servers = new FakeServers();
        var printer = new RecordingPrinter { HoldPrint = print };
        await RefusedWhileHeldThenAppliedAsync(servers, servers.AddJob(HostA), print, printer);
    }

    [Fact]
    public async Task ALinkOpenedWhileThePrintedReportIsDelayed_IsRefused_AndTheJobFinishesOnlyOnTheOldServer()
    {
        var report = new Gate();
        var servers = new FakeServers();
        servers.HoldPrintedReports.Enqueue(report);
        await RefusedWhileHeldThenAppliedAsync(servers, servers.AddJob(HostA), report, new RecordingPrinter());
    }

    [Fact]
    public async Task ALinkOpenedWhileThePrintedReportIsRetried_IsRefused_AndEveryAttemptGoesToTheOldServer()
    {
        // The first printed report fails on A (HTTP 500); the repeat is held while the link is opened.
        var retry = new Gate();
        var servers = new FakeServers { FailPrintedReports = 1 };
        servers.HoldPrintedReports.Enqueue(new Gate(open: true));
        servers.HoldPrintedReports.Enqueue(retry);
        var jobId = servers.AddJob(HostA);
        await RefusedWhileHeldThenAppliedAsync(servers, jobId, retry, new RecordingPrinter());
        Assert.Equal(["mark-printing", "mark-printed", "mark-printed"], servers.ReportsFor(jobId).Select(r => r.Report));
    }

    [Fact]
    public async Task AfterStopReturnedAtItsLimit_AJobStillFinishingKeepsTheGuard_UntilItIsDone()
    {
        var report = new Gate();
        var servers = new FakeServers();
        servers.HoldPrintedReports.Enqueue(report);
        var jobId = servers.AddJob(HostA);
        var printer = new RecordingPrinter();
        using var bridge = Bridge.Create(servers, printer);
        bridge.Runtime.StopGracePeriod = TimeSpan.FromMilliseconds(200);
        bridge.Runtime.Start();
        await report.Entered.Task.WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);
        Assert.False(bridge.Runtime.IsRunning);
        var before = bridge.Capture();

        // Listening has stopped, but the claimed job is still reporting: a stopped engine is not an idle one.
        var refused = await bridge.ApplyLinkAsync();
        AssertRefused(refused);
        bridge.AssertUnchanged(before);
        Assert.Equal(0, servers.Exchanges);

        // Start again: the new loop polls only after the old one has finished the job, so this poll proves it is done.
        var finished = servers.JobFinished(jobId);
        var firstPoll = servers.NextPoll(HostA);
        bridge.Runtime.Start();
        report.Open();
        await finished.WaitAsync(Wait, Ct);
        await firstPoll.WaitAsync(Wait, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.Connected, await bridge.ApplyLinkAsync());
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);
        AssertFinishedOnlyOnA(servers, printer, bridge, jobId, copies: 1);
    }

    [Fact]
    public async Task WhileSeveralJobsDrain_TheGuardStaysUntilTheLastOneIsDone()
    {
        var secondPrint = new Gate();
        var servers = new FakeServers();
        var first = servers.AddJob(HostA);
        var second = servers.AddJob(HostA);
        var printer = new RecordingPrinter { HoldPrint = secondPrint, HoldPrintNumber = 2 };
        using var bridge = Bridge.Create(servers, printer);
        bridge.Runtime.Start();
        await secondPrint.Entered.Task.WaitAsync(Wait, Ct);
        var before = bridge.Capture();

        // The first job is done, the second one is printing: still refused.
        Assert.Equal(JobState.Printed, servers.State(first));
        AssertRefused(await bridge.ApplyLinkAsync());
        bridge.AssertUnchanged(before);

        var finished = servers.JobFinished(second);
        secondPrint.Open();
        await finished.WaitAsync(Wait, Ct);
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct);

        Assert.Equal(PrintBridgeAutoSetupOutcome.Connected, await bridge.ApplyLinkAsync());
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);
        Assert.Equal(2, printer.Copies);
        foreach (var jobId in new[] { first, second })
        {
            Assert.Equal(JobState.Printed, servers.State(jobId));
            Assert.All(servers.ReportsFor(jobId), r => Assert.Equal((HostA, TokenA), (r.Host, r.Token)));
        }
    }

    [Fact]
    public async Task AJobOfferedWhileALinkIsBeingApplied_IsNotClaimed_AndStaysPendingOnTheOldServer()
    {
        // The other ordering before a claim: the link holds new jobs first (its exchange is in flight), then A offers one.
        var exchange = new Gate();
        var servers = new FakeServers { HoldExchange = exchange };
        var printer = new RecordingPrinter();
        using var bridge = Bridge.Create(servers, printer);
        bridge.Runtime.Start();
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct);

        var applying = bridge.ApplyLinkAsync();
        await exchange.Entered.Task.WaitAsync(Wait, Ct);
        var jobId = servers.AddJob(HostA);
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct); // this poll offers the job
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct); // the cycle that saw it is over
        Assert.Empty(servers.ReportsFor(jobId));

        exchange.Open();
        Assert.Equal(PrintBridgeAutoSetupOutcome.Connected, await applying.WaitAsync(Wait, Ct));
        await servers.NextPoll(HostB).WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Empty(servers.ReportsFor(jobId));
        Assert.Equal(JobState.Pending, servers.State(jobId));
        Assert.Equal(0, printer.Copies);
        Assert.Equal((UrlB, TokenB), bridge.SavedConnection());
    }

    [Fact]
    public async Task TheConnectionChangeLease_IsRefusedDuringAJob_HoldsNewJobs_AndIsReleasedOnlyOnce()
    {
        // The lease the classic window's Save and Test and the app's connection dialog take before changing anything.
        var print = new Gate();
        var servers = new FakeServers();
        var first = servers.AddJob(HostA);
        var printer = new RecordingPrinter { HoldPrint = print };
        using var bridge = Bridge.Create(servers, printer);
        bridge.Runtime.Start();
        await print.Entered.Task.WaitAsync(Wait, Ct);

        Assert.Null(bridge.Runtime.TryBeginConnectionChange());

        print.Open();
        await servers.JobFinished(first).WaitAsync(Wait, Ct);
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct);

        // Released twice: still counted once, so a second lease keeps holding new jobs.
        var released = bridge.Runtime.TryBeginConnectionChange();
        Assert.NotNull(released);
        released.Dispose();
        released.Dispose();
        var change = bridge.Runtime.TryBeginConnectionChange();
        Assert.NotNull(change);

        var second = servers.AddJob(HostA);
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct);
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct);
        Assert.Empty(servers.ReportsFor(second));

        var finished = servers.JobFinished(second);
        change.Dispose();
        await finished.WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);
        AssertFinishedOnlyOnA(servers, printer, bridge, second, copies: 2);
    }

    [Fact]
    public async Task AResetDuringPrinting_ClearsTheToken_ButTheJobStillFinishesWithTheTokenThatClaimedIt()
    {
        // Reset does not move to another server, so it is not refused. The printed report is built only after the token
        // was cleared, so only the job's own connection keeps it whole.
        var print = new Gate();
        var servers = new FakeServers();
        var jobId = servers.AddJob(HostA);
        var printer = new RecordingPrinter { HoldPrint = print };
        using var bridge = Bridge.Create(servers, printer);
        bridge.Runtime.StopGracePeriod = TimeSpan.FromMilliseconds(200);
        bridge.Runtime.Start();
        await print.Entered.Task.WaitAsync(Wait, Ct);

        await bridge.Runtime.ResetConnectionForReconnectAsync().WaitAsync(Wait, Ct);
        Assert.Equal(string.Empty, bridge.Holder.OrderHub.AgentToken);

        var finished = servers.JobFinished(jobId);
        print.Open();
        await finished.WaitAsync(Wait, Ct);

        Assert.Equal(["mark-printing", "mark-printed"], servers.ReportsFor(jobId).Select(r => r.Report));
        Assert.All(servers.ReportsFor(jobId), r => Assert.Equal((HostA, TokenA), (r.Host, r.Token)));
        Assert.Equal(JobState.Printed, servers.State(jobId));
        Assert.Equal(1, printer.Copies);
    }

    [Fact]
    public async Task ARefusedLink_LogsWhatHappened_WithoutAnyTokenOrSetupCode()
    {
        var report = new Gate();
        var servers = new FakeServers();
        servers.HoldPrintedReports.Enqueue(report);
        var logs = new CapturingLoggerFactory();
        await RefusedWhileHeldThenAppliedAsync(servers, servers.AddJob(HostA), report, new RecordingPrinter(), logs);

        var text = logs.Text();
        Assert.Contains("Automatic setup refused because a print job is still being completed", text, StringComparison.Ordinal);
        foreach (var secret in new[] { TokenA, TokenB, SetupCode, CompletionCredential })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Opens the link while <paramref name="held"/> holds the job: refused with nothing changed and no setup code used.
    /// Then lets the job finish on A, and opens the same link again, which now succeeds.
    /// </summary>
    private static async Task RefusedWhileHeldThenAppliedAsync(
        FakeServers servers,
        Guid jobId,
        Gate held,
        RecordingPrinter printer,
        ILoggerFactory? logs = null)
    {
        using var bridge = Bridge.Create(servers, printer, logs);
        bridge.Runtime.Start();
        await held.Entered.Task.WaitAsync(Wait, Ct);
        var before = bridge.Capture();

        var refused = await bridge.ApplyLinkAsync();

        AssertRefused(refused);
        bridge.AssertUnchanged(before);
        Assert.Equal(0, servers.Exchanges);

        var finished = servers.JobFinished(jobId);
        held.Open();
        await finished.WaitAsync(Wait, Ct);
        await servers.NextPoll(HostA).WaitAsync(Wait, Ct);

        // Idle now: the same link is applied, and from then on only B is used.
        var applied = await bridge.ApplyLinkAsync();
        Assert.Equal(PrintBridgeAutoSetupOutcome.Connected, applied);
        await servers.NextPoll(HostB).WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal((UrlB, TokenB), bridge.SavedConnection());
        AssertFinishedOnlyOnA(servers, printer, bridge, jobId, copies: 1);
    }

    /// <summary>The setup link was refused because a job is still being completed.</summary>
    private static void AssertRefused(PrintBridgeAutoSetupOutcome outcome) =>
        Assert.Equal(PrintBridgeAutoSetupOutcome.PrintingInProgress, outcome);

    /// <summary>Printed once, every report sent to A with A's token, confirmed on A, never failed.</summary>
    private static void AssertFinishedOnlyOnA(FakeServers servers, RecordingPrinter printer, Bridge bridge, Guid jobId, int copies)
    {
        Assert.Equal(copies, printer.Copies);
        Assert.Equal(JobState.Printed, servers.State(jobId));
        Assert.NotEmpty(servers.ReportsFor(jobId));
        Assert.All(servers.ReportsFor(jobId), r => Assert.Equal((HostA, TokenA), (r.Host, r.Token)));
        Assert.DoesNotContain(servers.ReportsFor(jobId), r => r.Report == "mark-failed");
        Assert.Equal(LocalPrintJobStatus.Printed, bridge.History.FindByJobId(jobId)?.Status);
    }

    private static string FirstInstalledPrinterOrSkip()
    {
        // Start() checks that the configured printer exists in Windows (read-only). Output goes to the recording fake.
        foreach (string installed in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            return installed;

        Assert.Skip("No Windows printer is installed; the runtime refuses to start without one.");
        return string.Empty;
    }

    private sealed record SettingsCapture(byte[] File, string Url, string Token, string InstallationId, string DisplayName);

    /// <summary>The engine, its settings and the setup coordinator, on the isolated data root, connected to A.</summary>
    private sealed record Bridge(
        PrintBridgeRuntime Runtime,
        PrintBridgeAutoSetupCoordinator Coordinator,
        PrintBridgeSettingsStore Store,
        PrintBridgeSettingsHolder Holder,
        LocalPrintJobHistoryStore History,
        HttpClient Http) : IDisposable
    {
        public static Bridge Create(FakeServers servers, RecordingPrinter printer, ILoggerFactory? logs = null)
        {
            var loggers = logs ?? NullLoggerFactory.Instance;
            var store = new PrintBridgeSettingsStore();
            var document = store.Load();
            document.OrderHub.ServerUrl = UrlA;
            document.OrderHub.AgentToken = TokenA;
            document.PrintBridge.PrinterName = FirstInstalledPrinterOrSkip();
            document.PrintBridge.DryRun = false;
            document.PrintBridge.DisplayName = "Kasa A";
            document.PrintBridge.ServerDeviceNameResolved = true;
            document.PrintBridge.IdlePollIntervalSeconds = 1;
            document.PrintBridge.BusyPollIntervalSeconds = 1;
            document.PrintBridge.ErrorPollIntervalSeconds = 1;
            store.Save(document);

            var holder = new PrintBridgeSettingsHolder();
            holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);
            var http = new HttpClient(servers, disposeHandler: false);
            var client = new WaslaPrintBridgeClient(
                http,
                holder,
                new PrintBridgeSetupHttpClientFactory(servers),
                loggers.CreateLogger<WaslaPrintBridgeClient>(),
                "1.0.0.0");
            var history = new LocalPrintJobHistoryStore(loggers.CreateLogger<LocalPrintJobHistoryStore>());
            var runtime = new PrintBridgeRuntime(
                client,
                new ReceiptFormatter(),
                printer,
                holder,
                store,
                new PrintBridgeDeviceMetadataSync(store, holder, loggers.CreateLogger<PrintBridgeDeviceMetadataSync>()),
                history,
                new AppVersionInfo(typeof(PrintBridgeSetupLinkGuardTests).Assembly),
                loggers.CreateLogger<PrintBridgeRuntime>())
            {
                PrintedReportRetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
            };
            var coordinator = CreateCoordinator(client, store, holder, runtime, loggers);
            return new Bridge(runtime, coordinator, store, holder, history, http);
        }

        private static PrintBridgeAutoSetupCoordinator CreateCoordinator(
            WaslaPrintBridgeClient client,
            PrintBridgeSettingsStore store,
            PrintBridgeSettingsHolder holder,
            PrintBridgeRuntime runtime,
            ILoggerFactory loggers) =>
            new(client, store, holder, runtime, loggers.CreateLogger<PrintBridgeAutoSetupCoordinator>());

        public Task<PrintBridgeAutoSetupOutcome> ApplyLinkAsync() => Coordinator.ApplyAsync(LinkToB, Ct);

        public (string Url, string Token) SavedConnection()
        {
            var saved = Store.Load().OrderHub;
            return (saved.ServerUrl, saved.AgentToken);
        }

        public SettingsCapture Capture()
        {
            var (hub, bridge, _) = Holder.Snapshot();
            return new SettingsCapture(
                System.IO.File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath),
                hub.ServerUrl,
                hub.AgentToken,
                bridge.InstallationId,
                bridge.DisplayName);
        }

        /// <summary>Neither the settings file nor the in-memory connection and device identity changed.</summary>
        public void AssertUnchanged(SettingsCapture before)
        {
            var after = Capture();
            Assert.Equal(before.File, after.File);
            Assert.Equal((before.Url, before.Token, before.InstallationId, before.DisplayName), (after.Url, after.Token, after.InstallationId, after.DisplayName));
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Http.Dispose();
        }
    }

    /// <summary>Holds a step after it started until the test opens it. Once opened it no longer holds.</summary>
    private sealed class Gate
    {
        public Gate(bool open = false)
        {
            if (open)
                Open();
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => Opened.TrySetResult();
    }

    private enum JobState
    {
        Pending,
        Printing,
        Printed,
        Failed
    }

    /// <summary>
    /// Two Wasla servers, A and B, each accepting only its own token and following the job contract of
    /// <c>PrintBridgeJobService</c>. B also answers the setup-link exchange with B's connection. Every request is recorded
    /// with the host and token it used. A request cancelled before it was sent, or while it was held, is not processed.
    /// </summary>
    private sealed class FakeServers : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, (string Host, JobState State)> _jobs = [];
        private readonly Dictionary<Guid, TaskCompletionSource> _finished = [];
        private readonly Dictionary<string, TaskCompletionSource> _nextPoll = [];
        private int _failPrintedReports;
        private int _exchanges;
        private bool _firstPollOfAHeld;

        public ConcurrentQueue<(string Host, string Token, string Path)> Requests { get; } = new();

        /// <summary>Holds A's first pending poll before it is answered.</summary>
        public Gate? HoldFirstPendingPollOfA { get; init; }

        /// <summary>Holds the setup-link exchange before B answers it.</summary>
        public Gate? HoldExchange { get; init; }

        /// <summary>Holds the claim (mark-printing) before A processes it.</summary>
        public Gate? HoldClaim { get; init; }

        /// <summary>Holds the answer to the claim after A has processed it.</summary>
        public Gate? HoldClaimAnswer { get; init; }

        /// <summary>One gate per printed report, in order, held before the server processes it.</summary>
        public ConcurrentQueue<Gate> HoldPrintedReports { get; } = new();

        /// <summary>How many printed reports are answered with HTTP 500 without being processed.</summary>
        public int FailPrintedReports
        {
            get => Volatile.Read(ref _failPrintedReports);
            init => _failPrintedReports = value;
        }

        public int Exchanges => Volatile.Read(ref _exchanges);

        public Guid AddJob(string host)
        {
            var jobId = Guid.NewGuid();
            lock (_sync)
            {
                _jobs[jobId] = (host, JobState.Pending);
                _finished[jobId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return jobId;
        }

        public JobState State(Guid jobId)
        {
            lock (_sync)
                return _jobs[jobId].State;
        }

        /// <summary>Completes when the job's final report (printed or failed) has been answered.</summary>
        public Task JobFinished(Guid jobId)
        {
            lock (_sync)
                return _finished[jobId].Task;
        }

        /// <summary>Completes at the next pending poll answered by the host.</summary>
        public Task NextPoll(string host)
        {
            lock (_sync)
            {
                if (!_nextPoll.TryGetValue(host, out var poll))
                    _nextPoll[host] = poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return poll.Task;
            }
        }

        /// <summary>Every report for the job that reached a server: report name, host and token.</summary>
        public (string Report, string Host, string Token)[] ReportsFor(Guid jobId) =>
            [.. Requests
                .Where(r => r.Path.Contains(jobId.ToString("D"), StringComparison.Ordinal))
                .Select(r => (r.Path[(r.Path.LastIndexOf('/') + 1)..], r.Host, r.Token))];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath.Trim('/');
            var token = request.Headers.TryGetValues("X-PrintBridge-Token", out var values) ? values.Single() : string.Empty;

            // A request cancelled before it was sent never reaches a server.
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue((host, token, path));

            if (path == "api/print-bridge/setup/exchange")
            {
                await HoldAsync(HoldExchange, cancellationToken);
                Interlocked.Increment(ref _exchanges);
                return host == HostB
                    ? Json(HttpStatusCode.OK, new
                    {
                        sessionId = Guid.NewGuid(),
                        serverUrl = UrlB,
                        deviceToken = TokenB,
                        deviceName = "Kasa B",
                        installationId = Guid.NewGuid(),
                        completionCredential = CompletionCredential
                    })
                    : Json(HttpStatusCode.NotFound, new { error = "setup_not_found" });
            }

            if (path == "api/print-bridge/setup/complete")
                return Json(HttpStatusCode.OK, new { success = true });

            var expectedToken = host == HostA ? TokenA : TokenB;
            if (!string.Equals(token, expectedToken, StringComparison.Ordinal))
                return Json(HttpStatusCode.Unauthorized, new { error = "device_auth_invalid" });

            if (path == "api/print-bridge/health")
                return Json(HttpStatusCode.OK, new { success = true, customerName = "QA", deviceName = host == HostA ? "Kasa A" : "Kasa B", serverTimeUtc = DateTime.UtcNow });

            if (path == "api/print-bridge/jobs/pending")
            {
                Gate? hold = null;
                lock (_sync)
                {
                    if (host == HostA && HoldFirstPendingPollOfA is { } gate && !_firstPollOfAHeld)
                    {
                        _firstPollOfAHeld = true;
                        hold = gate;
                    }
                }

                await HoldAsync(hold, cancellationToken);
                TaskCompletionSource? poll;
                object[] jobs;
                lock (_sync)
                {
                    _nextPoll.Remove(host, out poll);
                    jobs = [.. _jobs.Where(j => j.Value.Host == host && j.Value.State == JobState.Pending).Select(j => (object)new
                    {
                        id = j.Key,
                        orderId = Guid.NewGuid(),
                        type = "Receipt",
                        copyCount = 1,
                        payloadJson = """{"platform":"Getir","externalOrderCode":"QA-5801"}""",
                        createdAtUtc = DateTime.UtcNow
                    })];
                }

                poll?.TrySetResult();
                return Json(HttpStatusCode.OK, new { jobs });
            }

            var jobId = Guid.TryParse(path.Split('/')[^2], out var id) ? id : Guid.Empty;
            switch (path[(path.LastIndexOf('/') + 1)..])
            {
                case "mark-printing":
                {
                    await HoldAsync(HoldClaim, cancellationToken);
                    var answer = Transition(host, jobId, JobState.Pending, JobState.Printing);
                    await HoldAsync(HoldClaimAnswer, cancellationToken);
                    return answer;
                }

                case "mark-printed":
                {
                    HoldPrintedReports.TryDequeue(out var gate);
                    await HoldAsync(gate, cancellationToken);
                    if (Interlocked.Decrement(ref _failPrintedReports) >= 0)
                        return Json(HttpStatusCode.InternalServerError, new { error = "server_error" });

                    var answer = Transition(host, jobId, JobState.Printing, JobState.Printed);
                    Finish(jobId);
                    return answer;
                }

                case "mark-failed":
                {
                    var answer = Transition(host, jobId, JobState.Printing, JobState.Failed);
                    Finish(jobId);
                    return answer;
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private void Finish(Guid jobId)
        {
            lock (_sync)
            {
                if (_finished.TryGetValue(jobId, out var finished))
                    finished.TrySetResult();
            }
        }

        private HttpResponseMessage Transition(string host, Guid jobId, JobState from, JobState to)
        {
            lock (_sync)
            {
                if (!_jobs.TryGetValue(jobId, out var job) || job.Host != host)
                    return Json(HttpStatusCode.OK, new { success = false, skipped = false, result = "not_found" });

                if (job.State != from)
                    return Json(HttpStatusCode.OK, new { success = false, skipped = true, result = "skipped" });

                _jobs[jobId] = (host, to);
                return Json(HttpStatusCode.OK, new { success = true, skipped = false, result = "claimed" });
            }
        }

        private static async Task HoldAsync(Gate? gate, CancellationToken cancellationToken)
        {
            if (gate is null)
                return;

            gate.Entered.TrySetResult();
            await gate.Opened.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    /// <summary>Counts the copies the engine prints instead of printing, and can hold one print until opened.</summary>
    private sealed class RecordingPrinter : IReceiptPrinter
    {
        private int _copies;
        private int _prints;

        public Gate? HoldPrint { get; init; }

        /// <summary>Which print (1-based) <see cref="HoldPrint"/> holds.</summary>
        public int HoldPrintNumber { get; init; } = 1;

        public int Copies => Volatile.Read(ref _copies);

        public async Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _prints) == HoldPrintNumber && HoldPrint is { } gate)
            {
                gate.Entered.TrySetResult();
                await gate.Opened.Task;
            }

            Interlocked.Add(ref _copies, copyCount);
        }
    }

    /// <summary>Keeps every log message and exception text, at every level.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string Text() => string.Join(Environment.NewLine, _entries);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue($"{formatter(state, exception)} {state} {exception}");

        public void Dispose()
        {
        }
    }
}
