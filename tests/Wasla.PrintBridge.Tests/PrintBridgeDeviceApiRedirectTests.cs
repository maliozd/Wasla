using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-191: ordinary device requests (health, polling, job reports, reprint, device name and the manual-setup check)
/// never follow a redirect. The <see cref="WaslaPrintBridgeClient"/> comes from the app's own registration
/// (<see cref="PrintBridgeAppServices.AddWaslaApiClient"/>) and talks over real loopback sockets, so the production
/// handler decides. The server answers with a redirect to a second origin, which must receive nothing. The engine tests
/// below run the real <see cref="PrintBridgeRuntime"/> on that client with a recording printer; nothing is printed.
/// </summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class PrintBridgeDeviceApiRedirectTests : IDisposable
{
    private const string FakeToken = "device-token-not-a-real-credential";
    private const string CandidateToken = "candidate-token-not-a-real-credential";
    private const string FailureSentinel = "paper-out-sentinel";
    private const string DeviceNameSentinel = "Kasa-sentinel";
    private const string HealthPath = "api/print-bridge/health";
    private const string PendingPath = "api/print-bridge/jobs/pending";
    private static readonly Guid JobId = Guid.Parse("6f1f4a52-7d0e-4c55-9d0c-2a4b8f0e1d91");
    private static readonly int[] RedirectStatuses = [300, 301, 302, 303, 307, 308];

    private static readonly Dictionary<string, DeviceCall> Calls = new()
    {
        ["health"] = new("GET", HealthPath, FakeToken, null, (c, _) => c.TestHealthAsync(Ct)),
        ["manual-check"] = new("GET", HealthPath, CandidateToken, null,
            (c, url) => c.TestHealthAsync(new WaslaOptions { ServerUrl = url, AgentToken = CandidateToken }, Ct)),
        ["pending"] = new("GET", PendingPath, FakeToken, null, (c, _) => c.GetPendingJobsAsync(Ct)),
        ["claim"] = new("POST", JobPath("mark-printing"), FakeToken, null, (c, _) => c.MarkPrintingAsync(JobId, Ct)),
        ["printed"] = new("POST", JobPath("mark-printed"), FakeToken, null, (c, _) => c.MarkPrintedAsync(JobId, Ct)),
        ["failed"] = new("POST", JobPath("mark-failed"), FakeToken, FailureSentinel,
            (c, _) => c.MarkFailedAsync(JobId, FailureSentinel, Ct)),
        ["reprint"] = new("POST", JobPath("reprint"), FakeToken, null, (c, _) => c.RequestReprintAsync(JobId, Ct)),
        ["device-name"] = new("PUT", "api/print-bridge/device/name", FakeToken, DeviceNameSentinel,
            (c, _) => c.UpdateDeviceNameAsync(DeviceNameSentinel, Ct)),
    };

    // Each engine test disposes its engine (which waits for the polling loop) before this root is released.
    private readonly IsolatedDataRoot _dataRoot = new("wasla-pb-device-redirect-tests");

    public void Dispose() => _dataRoot.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, string> EveryRedirectOfEveryCall()
    {
        var data = new TheoryData<int, string>();
        foreach (var status in RedirectStatuses)
        {
            foreach (var call in Calls.Keys)
                data.Add(status, call);
        }

        return data;
    }

    public static TheoryData<int, string> EveryRedirectOfAReadAndTwoWrites()
    {
        var data = new TheoryData<int, string>();
        foreach (var status in RedirectStatuses)
        {
            foreach (var call in new[] { "health", "failed", "device-name" })
                data.Add(status, call);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRedirectOfEveryCall))]
    public async Task ARedirectedDeviceRequest_IsNotFollowed_AndFailsAsARequest_NotAsARejectedToken(int status, string callName)
    {
        var call = Calls[callName];
        using var elsewhere = new LoopbackSetupServer();
        // The redirect also carries a token error code: it must still not count as a rejected or removed device.
        using var server = new LoopbackSetupServer { Redirect = new LoopbackRedirect(status, elsewhere.Url, ErrorCode: "device_removed") };
        var logs = new CapturedLogs();
        using var app = ProductionApp.Create(server.Url, logs);

        var error = await Assert.ThrowsAsync<PrintBridgeConnectionException>(() => call.Send(app.Client, server.Url));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(PrintBridgeRuntimeIssueCode.RequestFailed, error.IssueCode);
        Assert.Equal("Connection.RedirectNotFollowed", error.UserMessageKey);
        Assert.False(error.IsTokenAuthFailure);
        Assert.False(new PrintBridgeRuntimeIssue(error.IssueCode, error.UserMessageKey).ShouldClearToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal((call.Method, call.Path, call.Token), (request.Method, request.Path, request.Token));
        if (call.BodySentinel is not null)
            Assert.Contains(call.BodySentinel, request.Body, StringComparison.Ordinal);
        Assert.Empty(elsewhere.Requests);

        Assert.Equal(FakeToken, app.Holder.Connection.AgentToken);
        var logText = logs.Text();
        Assert.DoesNotContain(FakeToken, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(CandidateToken, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(FailureSentinel, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceNameSentinel, logText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: the same client over the handler the app used before WAS-191 (the factory's default, which follows
    /// redirects) sends the token to the second origin, re-sends a body there (307/308; a PUT also on 300–302) and
    /// reports the redirected request as a success. The test servers therefore catch the original behavior.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRedirectOfAReadAndTwoWrites))]
    public async Task Control_AClientThatFollowsRedirects_SendsTheTokenAndBodyElsewhere_AndReportsSuccess(int status, string callName)
    {
        var call = Calls[callName];
        using var elsewhere = new LoopbackSetupServer();
        using var server = new LoopbackSetupServer { Redirect = new LoopbackRedirect(status, elsewhere.Url) };
        var holder = Holder(server.Url);
        using var following = new HttpClient(new HttpClientHandler());
        var client = new WaslaPrintBridgeClient(
            following,
            holder,
            new PrintBridgeSetupHttpClientFactory(),
            NullLogger<WaslaPrintBridgeClient>.Instance,
            "1.0.0.0");

        await call.Send(client, server.Url);

        var forwarded = Assert.Single(elsewhere.Requests);
        Assert.Equal(FakeToken, forwarded.Token);
        if (call.BodySentinel is not null)
        {
            var bodyResent = status is 307 or 308 || (call.Method == "PUT" && status != 303);
            Assert.Equal(bodyResent, forwarded.Body.Contains(call.BodySentinel, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheProductionDeviceClient_HasANoRedirectPrimaryHandler()
    {
        using var app = ProductionApp.Create("http://localhost", new CapturedLogs());

        HttpMessageHandler? handler = app.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(PrintBridgeAppServices.DeviceApiClientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler;

        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public async Task ADirectServer_StillAnswersEveryCallOverTheProductionHandler()
    {
        var api = new JobServer();
        api.Add(JobId);
        using var server = new LoopbackSetupServer(api);
        using var app = ProductionApp.Create(server.Url, new CapturedLogs());

        var health = await app.Client.TestHealthAsync(Ct);
        var candidate = await app.Client.TestHealthAsync(new WaslaOptions { ServerUrl = server.Url, AgentToken = CandidateToken }, Ct);
        var jobs = await app.Client.GetPendingJobsAsync(Ct);
        var claim = await app.Client.MarkPrintingAsync(JobId, Ct);
        var printed = await app.Client.MarkPrintedAsync(JobId, Ct);
        var repeated = await app.Client.MarkPrintedAsync(JobId, Ct);

        Assert.Equal(("QA", "Kasa 1"), (health.TenantName, health.DeviceName));
        Assert.Equal("QA", candidate.TenantName);
        Assert.Equal(JobId, Assert.Single(jobs).Id);
        Assert.True(claim.Success);
        Assert.True(printed.Success);
        Assert.True(repeated.Skipped);
        Assert.Equal("Printed", api.State(JobId));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(308)]
    public async Task ARedirectedPoll_KeepsTheTokenAndKeepsPolling_AndConnectsOnceTheRedirectIsGone(int status)
    {
        using var elsewhere = new LoopbackSetupServer();
        using var server = new LoopbackSetupServer(new JobServer())
        {
            Redirect = new LoopbackRedirect(status, elsewhere.Url, Path: HealthPath, ErrorCode: "device_removed")
        };
        using var engine = Engine.Create(server.Url);

        engine.Runtime.Start();
        await WaitUntilAsync(() => server.Count(HealthPath) >= 2);

        var status1 = engine.Runtime.GetStatus();
        Assert.True(engine.Runtime.IsRunning);
        Assert.Equal(PrintBridgeRuntimeIssueCode.RequestFailed, status1.LastIssue?.Code);
        Assert.Equal("Connection.RedirectNotFollowed", status1.LastIssue?.ResourceKey);
        Assert.Equal(FakeToken, engine.Holder.Connection.AgentToken);
        Assert.Equal(FakeToken, new PrintBridgeSettingsStore().Load().Wasla.AgentToken);
        Assert.Equal(0, server.Count(PendingPath));

        server.Redirect = null;
        await WaitUntilAsync(() => engine.Runtime.GetStatus().IsConnected);
        await engine.Runtime.StopAsync();

        Assert.Empty(elsewhere.Requests);
        Assert.Empty(engine.Printer.Calls);
    }

    /// <summary>
    /// A claim answered with a redirect is never printed: the server may or may not have claimed it. A job the server
    /// did not claim stays Pending and is printed once after the redirect is gone. A job it did claim stays in Printing
    /// and is never printed again automatically (no claim lease, see the Print Bridge known gaps).
    /// </summary>
    [Theory]
    [InlineData(302, false)]
    [InlineData(307, false)]
    [InlineData(307, true)]
    [InlineData(308, true)]
    public async Task ARedirectedClaim_IsNeverPrinted_AndOnlyAnUnclaimedJobIsPrintedOnceLater(int status, bool serverClaimedIt)
    {
        var claimPath = JobPath("mark-printing");
        var api = new JobServer();
        api.Add(JobId);
        using var elsewhere = new LoopbackSetupServer();
        using var server = new LoopbackSetupServer(api)
        {
            Redirect = new LoopbackRedirect(status, elsewhere.Url, Path: claimPath, AfterForwarding: serverClaimedIt)
        };
        using var engine = Engine.Create(server.Url);

        engine.Runtime.Start();
        await WaitUntilAsync(() => server.Count(claimPath) >= 1);
        var pollsAfterClaim = server.Count(PendingPath);
        await WaitUntilAsync(() => server.Count(PendingPath) >= pollsAfterClaim + 1);

        Assert.Empty(engine.Printer.Calls);
        Assert.Equal(serverClaimedIt ? "Printing" : "Pending", api.State(JobId));
        // An unclaimed job is offered and refused again on every poll, so it may be between the two.
        Assert.Contains(
            Assert.Single(engine.Runtime.GetStatus().RecentJobs).Status,
            new[] { LocalPrintJobStatus.Failed, LocalPrintJobStatus.Received });
        Assert.Contains(engine.Issues, issue => issue?.ResourceKey == "Connection.RedirectNotFollowed");

        server.Redirect = null;
        if (serverClaimedIt)
        {
            var polls = server.Count(PendingPath);
            await WaitUntilAsync(() => server.Count(PendingPath) >= polls + 2);
        }
        else
        {
            await WaitUntilAsync(() => api.State(JobId) == "Printed");
        }

        await engine.Runtime.StopAsync();

        Assert.Equal(serverClaimedIt ? 0 : 1, engine.Printer.Calls.Count);
        Assert.Equal(serverClaimedIt ? "Printing" : "Printed", api.State(JobId));
        Assert.Equal(0, server.Count(JobPath("mark-failed")));
        Assert.Equal(serverClaimedIt ? 0 : 1, server.Count(JobPath("mark-printed")));
        Assert.Empty(elsewhere.Requests);
    }

    /// <summary>
    /// A printed job whose mark-printed is answered with a redirect is reported again to the same server, as after a 5xx
    /// (WAS-56), and is never reported failed or printed twice. When every attempt is redirected, the loop records the
    /// problem; the server has the job Printed when it stored a report, and otherwise keeps it in Printing.
    /// </summary>
    [Theory]
    [InlineData(307, 1, false)]
    [InlineData(308, 1, true)]
    [InlineData(301, null, false)]
    [InlineData(307, null, true)]
    public async Task ARedirectedAcknowledgement_IsRepeatedToTheSameServer_AndTheJobStaysPrinted(
        int status,
        int? redirectedAttempts,
        bool serverStoredIt)
    {
        var printedPath = JobPath("mark-printed");
        var api = new JobServer();
        api.Add(JobId);
        using var elsewhere = new LoopbackSetupServer();
        using var server = new LoopbackSetupServer(api)
        {
            Redirect = new LoopbackRedirect(status, elsewhere.Url, Path: printedPath, AfterForwarding: serverStoredIt, Times: redirectedAttempts)
        };
        using var engine = Engine.Create(server.Url);
        engine.Runtime.PrintedReportRetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        var attempts = redirectedAttempts is null ? 1 + engine.Runtime.PrintedReportRetryDelays.Count : redirectedAttempts.Value + 1;

        engine.Runtime.Start();
        await WaitUntilAsync(() => server.Count(printedPath) >= attempts);
        var polls = server.Count(PendingPath);
        await WaitUntilAsync(() => server.Count(PendingPath) >= polls + 1);
        await engine.Runtime.StopAsync();

        Assert.Single(engine.Printer.Calls);
        Assert.Equal(attempts, server.Count(printedPath));
        Assert.Equal(0, server.Count(JobPath("mark-failed")));
        Assert.Equal(LocalPrintJobStatus.Printed, Assert.Single(engine.Runtime.GetStatus().RecentJobs).Status);
        Assert.Equal(redirectedAttempts is not null || serverStoredIt ? "Printed" : "Printing", api.State(JobId));
        Assert.Empty(elsewhere.Requests);
        Assert.Equal(FakeToken, engine.Holder.Connection.AgentToken);
        // When every report was redirected, the cycle that printed the job records the problem.
        Assert.Equal(
            redirectedAttempts is null,
            engine.Issues.Any(issue => issue?.ResourceKey == "Connection.RedirectNotFollowed"));
    }

    private static string JobPath(string action) => $"api/print-bridge/jobs/{JobId:D}/{action}";

    private static PrintBridgeSettingsHolder Holder(string serverUrl)
    {
        var document = new PrintBridgeSettingsStore().Load();
        document.Wasla.ServerUrl = serverUrl;
        document.Wasla.AgentToken = FakeToken;
        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.Wasla, document.PrintBridge, document.Ui);
        return holder;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");

            await Task.Delay(25, Ct);
        }
    }

    /// <summary>A device request: its method, path, the token it must carry, a sentinel in its body, and how to send it.</summary>
    private sealed record DeviceCall(
        string Method,
        string Path,
        string Token,
        string? BodySentinel,
        Func<WaslaPrintBridgeClient, string, Task> Send);

    /// <summary>The client exactly as the app registers it, over the settings in <see cref="Holder"/>.</summary>
    private sealed class ProductionApp(ServiceProvider services, PrintBridgeSettingsHolder holder) : IDisposable
    {
        public ServiceProvider Services { get; } = services;

        public PrintBridgeSettingsHolder Holder { get; } = holder;

        public WaslaPrintBridgeClient Client => Services.GetRequiredService<WaslaPrintBridgeClient>();

        public static ProductionApp Create(string serverUrl, CapturedLogs logs)
        {
            var holder = PrintBridgeDeviceApiRedirectTests.Holder(serverUrl);
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddSingleton(holder);
            PrintBridgeAppServices.AddWaslaApiClient(services, new AppVersionInfo(typeof(PrintBridgeDeviceApiRedirectTests).Assembly));
            return new ProductionApp(services.BuildServiceProvider(), holder);
        }

        public void Dispose() => Services.Dispose();
    }

    /// <summary>The real engine on the production client, with a recording printer and one-second polls.</summary>
    private sealed class Engine : IDisposable
    {
        private readonly ProductionApp _app;

        private Engine(ProductionApp app, PrintBridgeRuntime runtime, RecordingPrinter printer)
        {
            _app = app;
            Runtime = runtime;
            Printer = printer;
            Runtime.StatusChanged += (_, _) => Issues.Enqueue(Runtime.GetStatus().LastIssue);
        }

        /// <summary>The last issue at every status change, so a briefly recorded one is not missed.</summary>
        public ConcurrentQueue<PrintBridgeRuntimeIssue?> Issues { get; } = new();

        public PrintBridgeRuntime Runtime { get; }

        public RecordingPrinter Printer { get; }

        public PrintBridgeSettingsHolder Holder => _app.Holder;

        public static Engine Create(string serverUrl)
        {
            var printerName = FirstInstalledPrinterOrSkip();
            var store = new PrintBridgeSettingsStore();
            var document = store.Load();
            document.Wasla.ServerUrl = serverUrl;
            document.Wasla.AgentToken = FakeToken;
            document.PrintBridge.PrinterName = printerName;
            document.PrintBridge.DryRun = false;
            document.PrintBridge.IdlePollIntervalSeconds = 1;
            document.PrintBridge.BusyPollIntervalSeconds = 1;
            document.PrintBridge.ErrorPollIntervalSeconds = 1;
            store.Save(document);

            var app = ProductionApp.Create(serverUrl, new CapturedLogs());
            app.Holder.Replace(document.Wasla, document.PrintBridge, document.Ui);
            var printer = new RecordingPrinter();
            var runtime = new PrintBridgeRuntime(
                app.Client,
                new ReceiptFormatter(),
                printer,
                app.Holder,
                store,
                new PrintBridgeDeviceMetadataSync(store, app.Holder, NullLogger<PrintBridgeDeviceMetadataSync>.Instance),
                new LocalPrintJobHistoryStore(NullLogger<LocalPrintJobHistoryStore>.Instance),
                new AppVersionInfo(typeof(PrintBridgeDeviceApiRedirectTests).Assembly),
                NullLogger<PrintBridgeRuntime>.Instance);
            return new Engine(app, runtime, printer);
        }

        public void Dispose()
        {
            Runtime.Dispose();
            _app.Dispose();
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
    }

    private sealed class RecordingPrinter : IReceiptPrinter
    {
        public ConcurrentQueue<(string PrinterName, int CopyCount)> Calls { get; } = new();

        public Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
        {
            Calls.Enqueue((printerName, copyCount));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The server's job contract (<c>PrintBridgeJobService</c>): a poll offers only Pending jobs, and each report is a
    /// conditional transition; a report for a job in another state is answered skipped and changes nothing.
    /// </summary>
    private sealed class JobServer : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<Guid, string> _states = new();

        public void Add(Guid jobId) => _states[jobId] = "Pending";

        public string State(Guid jobId) => _states[jobId];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (path == HealthPath)
                return Json(new { success = true, tenantName = "QA", deviceName = "Kasa 1", serverTimeUtc = DateTime.UtcNow });

            if (path == PendingPath)
            {
                var jobs = _states.Where(s => s.Value == "Pending").Select(s => new
                {
                    id = s.Key,
                    orderId = Guid.Empty,
                    type = "Receipt",
                    copyCount = 1,
                    payloadJson = """{"platform":"Getir","externalOrderCode":"QA-1001"}""",
                    createdAtUtc = DateTime.UtcNow
                });
                return Json(new { jobs });
            }

            var parts = path.Split('/');
            if (parts.Length == 5 && Guid.TryParse(parts[3], out var jobId))
            {
                var (from, to) = parts[4] switch
                {
                    "mark-printing" => ("Pending", "Printing"),
                    "mark-printed" => ("Printing", "Printed"),
                    "mark-failed" => ("Printing", "Failed"),
                    _ => (null, null)
                };

                if (from is not null && to is not null)
                {
                    var changed = _states.TryUpdate(jobId, to, from);
                    return Json(new { success = changed, skipped = !changed, result = changed ? "ok" : "skipped" });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });
    }

    /// <summary>Every log line at every level, including the HTTP client factory's own.</summary>
    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string Text() => string.Join(Environment.NewLine, _entries);

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue($"{formatter(state, exception)} {state} {exception}");

        public void Dispose()
        {
        }
    }
}
