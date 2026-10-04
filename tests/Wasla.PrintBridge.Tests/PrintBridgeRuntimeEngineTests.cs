using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PrintBridgeDataRootCollection
{
    public const string Name = "Print Bridge isolated data root";
}

/// <summary>
/// Drives the real <see cref="PrintBridgeRuntime"/> from Wasla.PrintBridge.Core against a fake Wasla API
/// and a fake printer. Every path is redirected to a temporary directory, so the machine's real settings,
/// device token and print history are never read or written, and nothing is sent to a physical printer.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeRuntimeEngineTests : IDisposable
{
    private const string ServerUrl = "http://print-bridge.test";
    private const string FakeToken = "test-token-not-a-real-credential";

    private readonly string _root;
    private readonly IDisposable _rootScope;

    public PrintBridgeRuntimeEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wasla-pb-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _rootScope = PrintBridgePaths.UseRootForTests(_root);
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PollingDefaults_AreUnchanged()
    {
        var options = new PrintBridgeOptions();

        Assert.Equal(5, options.IdlePollIntervalSeconds);
        Assert.Equal(1, options.BusyPollIntervalSeconds);
        Assert.Equal(15, options.ErrorPollIntervalSeconds);
        Assert.Equal(3, options.MaxJobsPerPoll);
    }

    [Fact]
    public void Paths_AreRedirectedToTheIsolatedTestRoot()
    {
        Assert.StartsWith(_root, PrintBridgePaths.ProgramDataConfigPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_root, PrintBridgePaths.ProgramDataHistoryPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_root, PrintBridgePaths.ProgramDataLogDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WhenAlreadyRunning_DoesNotStartASecondPollingLoop()
    {
        var api = new FakeWaslaApi { HoldHealthRequests = true };
        using var harness = CreateHarness(api, dryRun: true);

        harness.Runtime.Start();
        harness.Runtime.Start();
        await api.FirstHealthRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.True(harness.Runtime.IsRunning);
        Assert.Equal(1, api.CountRequests("api/print-bridge/health"));
        Assert.Equal(1, api.MaxConcurrentHealthRequests);

        api.ReleaseHealthRequests();
        await harness.Runtime.StopAsync();
    }

    [Fact]
    public async Task PendingJob_IsClaimedThenPrintedWithItsCopyCountThenMarkedPrinted()
    {
        var jobId = Guid.NewGuid();
        var api = new FakeWaslaApi();
        api.EnqueueJob(jobId, copyCount: 2);
        using var harness = CreateHarness(api, dryRun: false);

        harness.Runtime.Start();
        await api.JobFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.Runtime.StopAsync();

        var print = Assert.Single(harness.Printer.Calls);
        Assert.Equal(harness.PrinterName, print.PrinterName);
        Assert.Equal(2, print.CopyCount);
        Assert.Equal(
            new[]
            {
                $"api/print-bridge/jobs/{jobId:D}/mark-printing",
                $"api/print-bridge/jobs/{jobId:D}/mark-printed"
            },
            api.JobActionPaths(jobId));

        var job = Assert.Single(harness.Runtime.GetStatus().RecentJobs);
        Assert.Equal(LocalPrintJobStatus.Printed, job.Status);
        Assert.Equal("QA-1001", job.OrderDisplay);
    }

    [Fact]
    public async Task ClaimSkipped_DoesNotPrintAndDoesNotMarkPrinted()
    {
        var jobId = Guid.NewGuid();
        var api = new FakeWaslaApi { ClaimResult = (Success: false, Skipped: true) };
        api.EnqueueJob(jobId, copyCount: 1);
        using var harness = CreateHarness(api, dryRun: false);

        harness.Runtime.Start();
        await api.ClaimAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Runtime.GetStatus().RecentJobs.Any(j => j.Status == LocalPrintJobStatus.Skipped));
        await harness.Runtime.StopAsync();

        Assert.Empty(harness.Printer.Calls);
        Assert.Equal(new[] { $"api/print-bridge/jobs/{jobId:D}/mark-printing" }, api.JobActionPaths(jobId));
    }

    [Fact]
    public async Task PrinterFailure_MarksTheJobFailedAndNeverPrinted()
    {
        var jobId = Guid.NewGuid();
        var api = new FakeWaslaApi();
        api.EnqueueJob(jobId, copyCount: 1);
        using var harness = CreateHarness(api, dryRun: false);
        harness.Printer.FailWith = new InvalidOperationException("Paper out");

        harness.Runtime.Start();
        await api.JobFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.Runtime.StopAsync();

        Assert.Equal(
            new[]
            {
                $"api/print-bridge/jobs/{jobId:D}/mark-printing",
                $"api/print-bridge/jobs/{jobId:D}/mark-failed"
            },
            api.JobActionPaths(jobId));
        Assert.Equal(LocalPrintJobStatus.Failed, Assert.Single(harness.Runtime.GetStatus().RecentJobs).Status);
    }

    [Fact]
    public async Task DryRun_SkipsThePrinterButStillCompletesTheJob()
    {
        var jobId = Guid.NewGuid();
        var api = new FakeWaslaApi();
        api.EnqueueJob(jobId, copyCount: 3);
        using var harness = CreateHarness(api, dryRun: true);

        harness.Runtime.Start();
        await api.JobFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.Runtime.StopAsync();

        Assert.Empty(harness.Printer.Calls);
        Assert.Contains($"api/print-bridge/jobs/{jobId:D}/mark-printed", api.JobActionPaths(jobId));
        Assert.True(File.Exists(Path.Combine(_root, "logs", "test-receipts", $"receipt-{jobId:N}.txt")));
    }

    [Fact]
    public async Task ReconnectRequiredResponse_ClearsOnlyTheTokenAndStopsPolling()
    {
        var api = new FakeWaslaApi { HealthFailure = (HttpStatusCode.Unauthorized, "device_auth_invalid") };
        using var harness = CreateHarness(api, dryRun: true);

        harness.Runtime.Start();
        await WaitUntilAsync(() => !harness.Runtime.IsRunning);

        var status = harness.Runtime.GetStatus();
        Assert.Equal(PrintBridgeRuntimeIssueCode.ReconnectRequired, status.LastIssue?.Code);
        Assert.False(status.IsConnected);
        Assert.Equal(string.Empty, harness.Holder.OrderHub.AgentToken);
        Assert.Equal(ServerUrl, harness.Holder.OrderHub.ServerUrl);
        Assert.Equal(harness.PrinterName, harness.Holder.Bridge.PrinterName);

        var persisted = File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath);
        Assert.DoesNotContain(FakeToken, persisted, StringComparison.Ordinal);
        Assert.Equal(1, api.CountRequests("api/print-bridge/health"));
    }

    [Fact]
    public async Task SuccessfulContact_IsReportedAsConnectedWithTheServerDeviceName()
    {
        var api = new FakeWaslaApi { HoldHealthRequests = false };
        using var harness = CreateHarness(api, dryRun: true);

        harness.Runtime.Start();
        await WaitUntilAsync(() => harness.Runtime.GetStatus().IsConnected);
        var status = harness.Runtime.GetStatus();
        await harness.Runtime.StopAsync();

        Assert.Equal(BridgeServerConnectionStatus.Connected, status.ServerConnectionStatus);
        Assert.Equal("Kasa 1", status.DisplayName);
        Assert.True(status.ServerDeviceNameResolved);
        Assert.NotNull(status.LastSuccessfulContactUtc);
    }

    private Harness CreateHarness(FakeWaslaApi api, bool dryRun)
    {
        var printerName = FirstInstalledPrinterOrSkip();
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.OrderHub.ServerUrl = ServerUrl;
        document.OrderHub.AgentToken = FakeToken;
        document.PrintBridge.PrinterName = printerName;
        document.PrintBridge.DryRun = dryRun;
        document.PrintBridge.IdlePollIntervalSeconds = 1;
        document.PrintBridge.BusyPollIntervalSeconds = 1;
        document.PrintBridge.ErrorPollIntervalSeconds = 1;
        store.Save(document);

        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);

        var http = new HttpClient(api);
        var client = new WaslaPrintBridgeClient(
            http,
            holder,
            new PrintBridgeSetupHttpClientFactory(),
            NullLogger<WaslaPrintBridgeClient>.Instance,
            "1.0.0.0");
        var printer = new RecordingPrinter();
        var runtime = new PrintBridgeRuntime(
            client,
            new ReceiptFormatter(),
            printer,
            holder,
            store,
            new PrintBridgeDeviceMetadataSync(store, holder, NullLogger<PrintBridgeDeviceMetadataSync>.Instance),
            new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance),
            new AppVersionInfo(typeof(PrintBridgeRuntimeEngineTests).Assembly),
            NullLogger<PrintBridgeRuntime>.Instance);

        return new Harness(runtime, printer, holder, printerName, http);
    }

    private static string FirstInstalledPrinterOrSkip()
    {
        // Start() checks that the configured printer exists in Windows (read-only enumeration).
        // Output always goes to the recording fake, never to this printer.
        foreach (string installed in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            return installed;

        Assert.Skip("No Windows printer is installed; the runtime refuses to start without one.");
        return string.Empty;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private sealed record Harness(
        PrintBridgeRuntime Runtime,
        RecordingPrinter Printer,
        PrintBridgeSettingsHolder Holder,
        string PrinterName,
        HttpClient Http) : IDisposable
    {
        public void Dispose()
        {
            Runtime.Dispose();
            Http.Dispose();
        }
    }

    private sealed class RecordingPrinter : IReceiptPrinter
    {
        public ConcurrentQueue<(string PrinterName, string Text, int CopyCount)> Calls { get; } = new();

        public Exception? FailWith { get; set; }

        public Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
        {
            if (FailWith is not null)
                throw FailWith;

            Calls.Enqueue((printerName, text, copyCount));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWaslaApi : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<object> _pendingJobs = new();
        private readonly TaskCompletionSource _releaseHealth = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _healthInFlight;
        private int _maxHealthInFlight;

        public bool HoldHealthRequests { get; init; }

        public (bool Success, bool Skipped) ClaimResult { get; init; } = (true, false);

        public (HttpStatusCode Status, string ErrorCode)? HealthFailure { get; init; }

        public TaskCompletionSource FirstHealthRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ClaimAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource JobFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrentHealthRequests => Volatile.Read(ref _maxHealthInFlight);

        public void ReleaseHealthRequests() => _releaseHealth.TrySetResult();

        public void EnqueueJob(Guid jobId, int copyCount) =>
            _pendingJobs.Enqueue(new
            {
                id = jobId,
                orderId = Guid.NewGuid(),
                type = "Receipt",
                copyCount,
                payloadJson = """{"platform":"Getir","externalOrderCode":"QA-1001"}""",
                createdAtUtc = DateTime.UtcNow
            });

        public int CountRequests(string path) => _requests.Count(r => r == path);

        public string[] JobActionPaths(Guid jobId) =>
            _requests.Where(r => r.Contains(jobId.ToString("D"), StringComparison.Ordinal)).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(FakeToken, request.Headers.GetValues("X-PrintBridge-Token").Single());

            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            _requests.Enqueue(path);

            if (path == "api/print-bridge/health")
                return await HealthAsync(cancellationToken);

            if (path == "api/print-bridge/jobs/pending")
            {
                var jobs = new List<object>();
                while (_pendingJobs.TryDequeue(out var job))
                    jobs.Add(job);
                return Json(new { jobs });
            }

            if (path.EndsWith("/mark-printing", StringComparison.Ordinal))
            {
                ClaimAttempted.TrySetResult();
                return Json(new { success = ClaimResult.Success, skipped = ClaimResult.Skipped, result = "claim" });
            }

            if (path.EndsWith("/mark-printed", StringComparison.Ordinal)
                || path.EndsWith("/mark-failed", StringComparison.Ordinal))
            {
                JobFinished.TrySetResult();
                return Json(new { success = true, skipped = false, result = "ok" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private async Task<HttpResponseMessage> HealthAsync(CancellationToken cancellationToken)
        {
            var inFlight = Interlocked.Increment(ref _healthInFlight);
            InterlockedMax(ref _maxHealthInFlight, inFlight);
            FirstHealthRequest.TrySetResult();
            try
            {
                if (HoldHealthRequests)
                    await _releaseHealth.Task.WaitAsync(cancellationToken);

                if (HealthFailure is { } failure)
                {
                    return new HttpResponseMessage(failure.Status)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { error = failure.ErrorCode }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return Json(new
                {
                    success = true,
                    customerName = "QA",
                    deviceName = "Kasa 1",
                    serverTimeUtc = DateTime.UtcNow
                });
            }
            finally
            {
                Interlocked.Decrement(ref _healthInFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                   && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
    }
}
