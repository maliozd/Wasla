using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-56: Stop while a print job is being claimed, printed or acknowledged. The real engine runs against a fake server
/// that follows the job contract of <c>PrintBridgeJobService</c> and a printer that only records copies. Every step is
/// held with a gate and Stop is requested while it is held, so each phase is reached deterministically. A restart uses
/// a new engine on the same data root, as after closing and reopening Print Bridge.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeStopRaceTests : IDisposable
{
    private const string ServerUrl = "http://print-bridge.test";
    private const string FakeToken = "test-token-not-a-real-credential";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-stop-tests");

    public void Dispose() => _dataRoot.Dispose();

    [Fact]
    public async Task StopBeforeTheClaim_LeavesTheJobPending_AndARestartPrintsItOnce()
    {
        var poll = new Gate();
        var server = new FakePrintServer { HoldPendingPoll = poll };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await poll.Entered.Task.WaitAsync(Wait, Ct);

            await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);
            poll.Open();

            Assert.Equal(0, printer.Copies);
            Assert.Empty(server.Attempted(jobId));
            Assert.Equal(JobState.Pending, server.State(jobId));
            Assert.Null(bridge.History.FindByJobId(jobId));
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);

        AssertPrintedOnceAndConfirmed(server, printer, restarted, jobId, copies: 1);
    }

    [Fact]
    public async Task StopWhileTheClaimIsInFlight_FinishesTheClaimedJobOnce_AndARestartDoesNotPrintAgain()
    {
        var claim = new Gate();
        var server = new FakePrintServer { HoldClaim = claim };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await claim.Entered.Task.WaitAsync(Wait, Ct);

            var stopping = bridge.Runtime.StopAsync();
            claim.Open();
            await stopping.WaitAsync(Wait, Ct);

            AssertPrintedOnceAndConfirmed(server, printer, bridge, jobId, copies: 1);
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        AssertPrintedOnceAndConfirmed(server, printer, restarted, jobId, copies: 1);
    }

    [Fact]
    public async Task StopDuringThePhysicalPrint_FinishesEveryCopy_ConfirmsIt_AndNeverReportsFailed()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 2);
        var copy = new Gate();
        var printer = new RecordingPrinter { HoldFirstCopy = copy };
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await copy.Entered.Task.WaitAsync(Wait, Ct);

            var stopping = bridge.Runtime.StopAsync();
            copy.Open();
            await stopping.WaitAsync(Wait, Ct);

            AssertPrintedOnceAndConfirmed(server, printer, bridge, jobId, copies: 2);
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        AssertPrintedOnceAndConfirmed(server, printer, restarted, jobId, copies: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopWhileThePrintedReportIsInFlight_ConfirmsThePrintedJob_AndNeverReportsFailed(bool serverAlreadyProcessedIt)
    {
        var report = new Gate();
        var server = serverAlreadyProcessedIt
            ? new FakePrintServer { HoldPrintedReportAnswer = report }
            : new FakePrintServer { HoldPrintedReport = report };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await report.Entered.Task.WaitAsync(Wait, Ct);

            var stopping = bridge.Runtime.StopAsync();
            report.Open();
            await stopping.WaitAsync(Wait, Ct);

            AssertPrintedOnceAndConfirmed(server, printer, bridge, jobId, copies: 1);
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        AssertPrintedOnceAndConfirmed(server, printer, restarted, jobId, copies: 1);
    }

    [Fact]
    public async Task StopAfterTheJobIsConfirmed_ChangesNothing_AndARestartDoesNotPrintAgain()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            await bridge.StartAndSettleAsync(server);

            await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

            AssertPrintedOnceAndConfirmed(server, printer, bridge, jobId, copies: 1);
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        AssertPrintedOnceAndConfirmed(server, printer, restarted, jobId, copies: 1);
    }

    [Fact]
    public async Task StopDuringARealPrinterFailure_ReportsTheFailureOnce_AndARestartDoesNotPrintIt()
    {
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var copy = new Gate();
        var printer = new RecordingPrinter
        {
            HoldFirstCopy = copy,
            Failure = new InvalidOperationException("Printer offline.")
        };
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await copy.Entered.Task.WaitAsync(Wait, Ct);

            var stopping = bridge.Runtime.StopAsync();
            copy.Open();
            await stopping.WaitAsync(Wait, Ct);

            // Nothing was printed, so the job is reported failed (and can be reprinted from Wasla); never printed.
            Assert.Equal(0, printer.Copies);
            Assert.Equal(JobState.Failed, server.State(jobId));
            Assert.Equal(["mark-printing", "mark-failed"], server.Received(jobId));
            Assert.Equal(["mark-printing", "mark-failed"], server.Attempted(jobId));
            Assert.Equal(LocalPrintJobStatus.Failed, bridge.History.FindByJobId(jobId)?.Status);
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        Assert.Equal(0, printer.Copies);
        Assert.Equal(["mark-printing", "mark-failed"], server.Attempted(jobId));
    }

    [Fact]
    public async Task StopDuringARealAcknowledgementFailure_RetriesThePrintedReport_AndNeverReportsFailed()
    {
        var report = new Gate();
        var server = new FakePrintServer { HoldPrintedReport = report, FailPrintedReports = 1 };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await report.Entered.Task.WaitAsync(Wait, Ct);

            // The held report then fails on the server (HTTP 500); the next attempt is accepted.
            var stopping = bridge.Runtime.StopAsync();
            report.Open();
            await stopping.WaitAsync(Wait, Ct);

            Assert.Equal(1, printer.Copies);
            Assert.Equal(JobState.Printed, server.State(jobId));
            Assert.Equal(["mark-printing", "mark-printed", "mark-printed"], server.Attempted(jobId));
            Assert.Equal(LocalPrintJobStatus.Printed, bridge.History.FindByJobId(jobId)?.Status);
        }

        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        Assert.Equal(1, printer.Copies);
        Assert.Equal(["mark-printing", "mark-printed", "mark-printed"], server.Attempted(jobId));
    }

    [Fact]
    public async Task ARestartAfterAStopDuringTheAcknowledgement_ReadsThePrintedJobFromDisk_AndNeverPrintsItAgain()
    {
        var report = new Gate();
        var server = new FakePrintServer { HoldPrintedReport = report };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.Start();
            await report.Entered.Task.WaitAsync(Wait, Ct);
            var stopping = bridge.Runtime.StopAsync();
            report.Open();
            await stopping.WaitAsync(Wait, Ct);
        }

        // A new engine with a new history store, loaded from the files the first one wrote.
        using var restarted = Bridge.Create(server, printer);
        Assert.Equal(LocalPrintJobStatus.Printed, restarted.History.FindByJobId(jobId)?.Status);
        await restarted.StartAndSettleAsync(server);
        await restarted.Runtime.StopAsync().WaitAsync(Wait, Ct);

        // Wasla shows it printed, so nobody is prompted to reprint it, and Print Bridge never prints it again.
        Assert.Equal(1, printer.Copies);
        Assert.Equal(JobState.Printed, server.State(jobId));
        Assert.DoesNotContain("mark-failed", server.Attempted(jobId));
        using var reread = Bridge.Create(server, printer);
        Assert.Equal(LocalPrintJobStatus.Printed, reread.History.FindByJobId(jobId)?.Status);
    }

    [Fact]
    public async Task StopWaitsOnlyTheGracePeriod_AndTheJobStillFinishes_BeforeANewStartPollsAgain()
    {
        var report = new Gate();
        var server = new FakePrintServer { HoldPrintedReport = report };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using var bridge = Bridge.Create(server, printer);
        bridge.Runtime.StopGracePeriod = TimeSpan.FromMilliseconds(200);
        bridge.Runtime.Start();
        await report.Entered.Task.WaitAsync(Wait, Ct);

        // The printed report is still held, yet Stop returns after the grace period instead of waiting for it.
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);
        Assert.False(bridge.Runtime.IsRunning);
        Assert.Equal(JobState.Printing, server.State(jobId));

        // Starting again: the new loop polls only after the old one has finished its job.
        var firstPoll = server.NextPoll();
        bridge.Runtime.Start();
        report.Open();
        var statesAtFirstPoll = await firstPoll.WaitAsync(Wait, Ct);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(JobState.Printed, statesAtFirstPoll[jobId]);
        AssertPrintedOnceAndConfirmed(server, printer, bridge, jobId, copies: 1);
    }

    [Fact]
    public async Task APrintedReportThatKeepsFailing_IsNeverTurnedIntoAFailure_AndARestartDoesNotPrintAgain()
    {
        var server = new FakePrintServer { FailPrintedReports = int.MaxValue };
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using (var bridge = Bridge.Create(server, printer))
        {
            bridge.Runtime.PrintedReportRetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
            await bridge.StartAndSettleAsync(server);
            await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

            // Three attempts, then the loop records the connection problem; the job is never reported failed.
            Assert.Equal(1, printer.Copies);
            Assert.Equal(["mark-printing", "mark-printed", "mark-printed", "mark-printed"], server.Attempted(jobId));
            Assert.Equal(JobState.Printing, server.State(jobId));
            Assert.Equal(LocalPrintJobStatus.Printed, bridge.History.FindByJobId(jobId)?.Status);
        }

        // Without a lease the server does not offer the job again, so nothing is printed twice.
        using var restarted = Bridge.Create(server, printer);
        await restarted.StartAndSettleAsync(server);
        Assert.Equal(1, printer.Copies);
        Assert.DoesNotContain("mark-failed", server.Attempted(jobId));
    }

    [Fact]
    public async Task AStatusObserverThatFailsAfterThePrint_NeverTurnsThePrintedReceiptIntoAFailure()
    {
        // WAS-59: an exiting tray's status handler once threw at exactly this point. Whatever an observer does after the
        // receipt was printed, the job is never reported failed.
        var server = new FakePrintServer();
        var jobId = server.AddJob(copies: 1);
        var printer = new RecordingPrinter();
        using var bridge = Bridge.Create(server, printer);
        var failed = 0;
        bridge.Runtime.StatusChanged += (_, _) =>
        {
            if (printer.Copies > 0 && Interlocked.Exchange(ref failed, 1) == 0)
                throw new InvalidOperationException("A status observer failed after the print.");
        };

        await bridge.StartAndSettleAsync(server);
        await bridge.Runtime.StopAsync().WaitAsync(Wait, Ct);

        Assert.Equal(1, failed);
        Assert.Equal(1, printer.Copies);
        Assert.DoesNotContain("mark-failed", server.Attempted(jobId));
        Assert.Equal(LocalPrintJobStatus.Printed, bridge.History.FindByJobId(jobId)?.Status);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Printed exactly once with every copy, confirmed on the server and locally, and never reported failed.</summary>
    private static void AssertPrintedOnceAndConfirmed(FakePrintServer server, RecordingPrinter printer, Bridge bridge, Guid jobId, int copies)
    {
        Assert.Equal(copies, printer.Copies);
        Assert.Equal(JobState.Printed, server.State(jobId));
        Assert.Equal(["mark-printing", "mark-printed"], server.Received(jobId));
        Assert.DoesNotContain("mark-failed", server.Attempted(jobId));
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

    /// <summary>The engine with its settings, history and client, on the isolated data root.</summary>
    private sealed record Bridge(PrintBridgeRuntime Runtime, LocalPrintJobHistoryStore History, HttpClient Http) : IDisposable
    {
        public static Bridge Create(FakePrintServer server, RecordingPrinter printer)
        {
            var store = new PrintBridgeSettingsStore();
            var document = store.Load();
            if (string.IsNullOrEmpty(document.OrderHub.AgentToken))
            {
                document.OrderHub.ServerUrl = ServerUrl;
                document.OrderHub.AgentToken = FakeToken;
                document.PrintBridge.PrinterName = FirstInstalledPrinterOrSkip();
                document.PrintBridge.DryRun = false;
                document.PrintBridge.IdlePollIntervalSeconds = 1;
                document.PrintBridge.BusyPollIntervalSeconds = 1;
                document.PrintBridge.ErrorPollIntervalSeconds = 1;
                store.Save(document);
            }

            var holder = new PrintBridgeSettingsHolder();
            holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);
            var http = new HttpClient(server, disposeHandler: false);
            var client = new WaslaPrintBridgeClient(
                http,
                holder,
                new PrintBridgeSetupHttpClientFactory(),
                NullLogger<WaslaPrintBridgeClient>.Instance,
                "1.0.0.0");
            var history = new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance);
            var runtime = new PrintBridgeRuntime(
                client,
                new ReceiptFormatter(),
                printer,
                holder,
                store,
                new PrintBridgeDeviceMetadataSync(store, holder, NullLogger<PrintBridgeDeviceMetadataSync>.Instance),
                history,
                new AppVersionInfo(typeof(PrintBridgeStopRaceTests).Assembly),
                NullLogger<PrintBridgeRuntime>.Instance);
            return new Bridge(runtime, history, http);
        }

        /// <summary>Starts listening and waits for two polls, so every job offered by the first one has been handled.</summary>
        public async Task StartAndSettleAsync(FakePrintServer server)
        {
            var first = server.NextPoll();
            Runtime.Start();
            await first.WaitAsync(Wait, Ct);
            await server.NextPoll().WaitAsync(Wait, Ct);
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
    /// The server side of the job contract (<c>PrintBridgeJobService</c>): a poll offers only Pending jobs, and each report
    /// is a conditional transition (Pending → Printing → Printed or Failed) answered "skipped" when the job is not in the
    /// expected state. There is no lease: a job left in Printing is never offered again. As on a real connection, a
    /// request whose cancellation was requested before it was sent, or while it was held, is not processed.
    /// </summary>
    private sealed class FakePrintServer : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, (JobState State, int Copies)> _jobs = [];
        private readonly ConcurrentQueue<(Guid JobId, string Report)> _attempted = new();
        private readonly ConcurrentQueue<(Guid JobId, string Report)> _received = new();
        private TaskCompletionSource<IReadOnlyDictionary<Guid, JobState>>? _nextPoll;
        private int _failPrintedReports;

        /// <summary>Holds the pending poll before it is answered.</summary>
        public Gate? HoldPendingPoll { get; init; }

        /// <summary>Holds the claim (mark-printing) before the server processes it.</summary>
        public Gate? HoldClaim { get; init; }

        /// <summary>Holds the printed report (mark-printed) before the server processes it.</summary>
        public Gate? HoldPrintedReport { get; init; }

        /// <summary>Holds the answer to the printed report after the server has processed it.</summary>
        public Gate? HoldPrintedReportAnswer { get; init; }

        /// <summary>How many printed reports the server answers with HTTP 500 without processing them.</summary>
        public int FailPrintedReports
        {
            get => Volatile.Read(ref _failPrintedReports);
            init => _failPrintedReports = value;
        }

        public Guid AddJob(int copies)
        {
            var jobId = Guid.NewGuid();
            lock (_sync)
                _jobs[jobId] = (JobState.Pending, copies);
            return jobId;
        }

        public JobState State(Guid jobId)
        {
            lock (_sync)
                return _jobs[jobId].State;
        }

        /// <summary>Every report the client sent or tried to send for the job, in order.</summary>
        public string[] Attempted(Guid jobId) => [.. _attempted.Where(r => r.JobId == jobId).Select(r => r.Report)];

        /// <summary>The reports the server processed for the job, in order.</summary>
        public string[] Received(Guid jobId) => [.. _received.Where(r => r.JobId == jobId).Select(r => r.Report)];

        /// <summary>Completes at the next pending poll with the job states the server had when it answered.</summary>
        public Task<IReadOnlyDictionary<Guid, JobState>> NextPoll()
        {
            lock (_sync)
                return (_nextPoll ??= new TaskCompletionSource<IReadOnlyDictionary<Guid, JobState>>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var action = segments[^1];
            var jobId = segments.Length >= 2 && Guid.TryParse(segments[^2], out var id) ? id : Guid.Empty;
            if (jobId != Guid.Empty)
                _attempted.Enqueue((jobId, action));

            // A request cancelled before it was sent never reaches the server.
            cancellationToken.ThrowIfCancellationRequested();

            switch (action)
            {
                case "health":
                    return Json(HttpStatusCode.OK, new { success = true, customerName = "QA", deviceName = "Kasa 1", serverTimeUtc = DateTime.UtcNow });

                case "pending":
                {
                    await HoldAsync(HoldPendingPoll, cancellationToken);
                    TaskCompletionSource<IReadOnlyDictionary<Guid, JobState>>? poll;
                    IReadOnlyDictionary<Guid, JobState> states;
                    object[] jobs;
                    lock (_sync)
                    {
                        poll = _nextPoll;
                        _nextPoll = null;
                        states = _jobs.ToDictionary(j => j.Key, j => j.Value.State);
                        jobs = [.. _jobs.Where(j => j.Value.State == JobState.Pending).Select(j => (object)new
                        {
                            id = j.Key,
                            orderId = Guid.NewGuid(),
                            type = "Receipt",
                            copyCount = j.Value.Copies,
                            payloadJson = """{"platform":"Getir","externalOrderCode":"QA-5601"}""",
                            createdAtUtc = DateTime.UtcNow
                        })];
                    }

                    poll?.TrySetResult(states);
                    return Json(HttpStatusCode.OK, new { jobs });
                }

                case "mark-printing":
                    await HoldAsync(HoldClaim, cancellationToken);
                    return Transition(jobId, action, JobState.Pending, JobState.Printing);

                case "mark-printed":
                {
                    await HoldAsync(HoldPrintedReport, cancellationToken);
                    if (Interlocked.Decrement(ref _failPrintedReports) >= 0)
                        return Json(HttpStatusCode.InternalServerError, new { error = "server_error" });

                    var answer = Transition(jobId, action, JobState.Printing, JobState.Printed);
                    await HoldAsync(HoldPrintedReportAnswer, cancellationToken);
                    return answer;
                }

                case "mark-failed":
                    return Transition(jobId, action, JobState.Printing, JobState.Failed);

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private HttpResponseMessage Transition(Guid jobId, string report, JobState from, JobState to)
        {
            lock (_sync)
            {
                _received.Enqueue((jobId, report));
                if (!_jobs.TryGetValue(jobId, out var job))
                    return Json(HttpStatusCode.OK, new { success = false, skipped = false, result = "not_found" });

                if (job.State != from)
                    return Json(HttpStatusCode.OK, new { success = false, skipped = true, result = "skipped" });

                _jobs[jobId] = (to, job.Copies);
                return Json(HttpStatusCode.OK, new { success = true, skipped = false, result = "claimed" });
            }
        }

        private static async Task HoldAsync(Gate? gate, CancellationToken cancellationToken)
        {
            if (gate is null)
                return;

            gate.Entered.TrySetResult();
            await gate.Opened.Task.WaitAsync(cancellationToken);
            // A request whose cancellation was requested while it was held does not go on.
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// Records the copies the engine prints instead of printing. Like <see cref="WindowsReceiptPrinter"/>, it checks for
    /// cancellation before each copy, and a copy that has started (held by the gate) always completes or fails.
    /// </summary>
    private sealed class RecordingPrinter : IReceiptPrinter
    {
        private int _copies;

        public Gate? HoldFirstCopy { get; init; }

        /// <summary>When set, the printer fails instead of printing the first copy.</summary>
        public Exception? Failure { get; init; }

        public int Copies => Volatile.Read(ref _copies);

        public async Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
        {
            for (var copy = 0; copy < copyCount; copy++)
            {
                ct.ThrowIfCancellationRequested();
                if (copy == 0 && HoldFirstCopy is { } gate)
                {
                    gate.Entered.TrySetResult();
                    await gate.Opened.Task;
                }

                if (Failure is { } failure)
                    throw failure;

                Interlocked.Increment(ref _copies);
            }
        }
    }
}
