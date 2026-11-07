using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// Shared by the WAS-191 device API tests: the device requests, the fake token and body sentinels they carry, and the
/// settings they run on. Nothing here is a real credential.
/// </summary>
internal static class DeviceApi
{
    public const string FakeToken = "device-token-not-a-real-credential";
    public const string CandidateToken = "candidate-token-not-a-real-credential";
    public const string FailureSentinel = "paper-out-sentinel";
    public const string DeviceNameSentinel = "Kasa-sentinel";
    public const string HealthPath = "api/print-bridge/health";
    public const string PendingPath = "api/print-bridge/jobs/pending";
    public static readonly Guid JobId = Guid.Parse("6f1f4a52-7d0e-4c55-9d0c-2a4b8f0e1d91");

    /// <summary>Every ordinary device request, including the connection dialog's check with a candidate token.</summary>
    public static readonly IReadOnlyDictionary<string, DeviceCall> Calls = new Dictionary<string, DeviceCall>
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string JobPath(string action) => $"api/print-bridge/jobs/{JobId:D}/{action}";

    /// <summary>Settings on <paramref name="serverUrl"/> with <see cref="FakeToken"/>; needs an isolated data root.</summary>
    public static PrintBridgeSettingsHolder Holder(string serverUrl)
    {
        var document = new PrintBridgeSettingsStore().Load();
        document.Wasla.ServerUrl = serverUrl;
        document.Wasla.AgentToken = FakeToken;
        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.Wasla, document.PrintBridge, document.Ui);
        return holder;
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");

            await Task.Delay(25, Ct);
        }
    }
}

/// <summary>A device request: its method, path, the token it must carry, a sentinel in its body, and how to send it.</summary>
internal sealed record DeviceCall(
    string Method,
    string Path,
    string Token,
    string? BodySentinel,
    Func<WaslaPrintBridgeClient, string, Task> Send);

/// <summary>
/// The client exactly as the app registers it (<see cref="PrintBridgeAppServices.AddWaslaApiClient"/>), over the
/// settings in <see cref="DeviceApi.Holder"/>, logging at every level into <see cref="CapturedLogs"/>.
/// </summary>
internal sealed class DeviceApiApp(ServiceProvider services, PrintBridgeSettingsHolder holder) : IDisposable
{
    public ServiceProvider Services { get; } = services;

    public PrintBridgeSettingsHolder Holder { get; } = holder;

    public WaslaPrintBridgeClient Client => Services.GetRequiredService<WaslaPrintBridgeClient>();

    public static DeviceApiApp Create(string serverUrl, CapturedLogs logs)
    {
        var holder = DeviceApi.Holder(serverUrl);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton(holder);
        PrintBridgeAppServices.AddWaslaApiClient(services, new AppVersionInfo(typeof(DeviceApiApp).Assembly));
        return new DeviceApiApp(services.BuildServiceProvider(), holder);
    }

    public void Dispose() => Services.Dispose();
}

/// <summary>
/// The real engine on the production client, with a recording printer and one-second polls. It logs into the same
/// <see cref="CapturedLogs"/> as the client.
/// </summary>
internal sealed class DeviceApiEngine : IDisposable
{
    private readonly DeviceApiApp _app;

    private DeviceApiEngine(DeviceApiApp app, PrintBridgeRuntime runtime, DeviceApiPrinter printer)
    {
        _app = app;
        Runtime = runtime;
        Printer = printer;
        Runtime.StatusChanged += (_, _) => Issues.Enqueue(Runtime.GetStatus().LastIssue);
    }

    /// <summary>The last issue at every status change, so a briefly recorded one is not missed.</summary>
    public ConcurrentQueue<PrintBridgeRuntimeIssue?> Issues { get; } = new();

    public PrintBridgeRuntime Runtime { get; }

    public DeviceApiPrinter Printer { get; }

    public PrintBridgeSettingsHolder Holder => _app.Holder;

    public static DeviceApiEngine Create(string serverUrl, CapturedLogs? logs = null)
    {
        var printerName = FirstInstalledPrinterOrSkip();
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.Wasla.ServerUrl = serverUrl;
        document.Wasla.AgentToken = DeviceApi.FakeToken;
        document.PrintBridge.PrinterName = printerName;
        document.PrintBridge.DryRun = false;
        document.PrintBridge.IdlePollIntervalSeconds = 1;
        document.PrintBridge.BusyPollIntervalSeconds = 1;
        document.PrintBridge.ErrorPollIntervalSeconds = 1;
        store.Save(document);

        var app = DeviceApiApp.Create(serverUrl, logs ?? new CapturedLogs());
        app.Holder.Replace(document.Wasla, document.PrintBridge, document.Ui);
        var loggers = app.Services.GetRequiredService<ILoggerFactory>();
        var printer = new DeviceApiPrinter();
        var runtime = new PrintBridgeRuntime(
            app.Client,
            new ReceiptFormatter(),
            printer,
            app.Holder,
            store,
            new PrintBridgeDeviceMetadataSync(store, app.Holder, loggers.CreateLogger<PrintBridgeDeviceMetadataSync>()),
            new LocalPrintJobHistoryStore(loggers.CreateLogger<LocalPrintJobHistoryStore>()),
            new AppVersionInfo(typeof(DeviceApiEngine).Assembly),
            loggers.CreateLogger<PrintBridgeRuntime>());
        return new DeviceApiEngine(app, runtime, printer);
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

/// <summary>Records every print instead of printing; throws <see cref="Failure"/> when it is set.</summary>
internal sealed class DeviceApiPrinter : IReceiptPrinter
{
    public ConcurrentQueue<(string PrinterName, int CopyCount)> Calls { get; } = new();

    public Exception? Failure { get; set; }

    public Task PrintAsync(string printerName, string text, int copyCount, CancellationToken ct)
    {
        if (Failure is not null)
            throw Failure;

        Calls.Enqueue((printerName, copyCount));
        return Task.CompletedTask;
    }
}

/// <summary>
/// The server's job contract (<c>PrintBridgeJobService</c>): a poll offers only Pending jobs, and each report is a
/// conditional transition; a report for a job in another state is answered skipped and changes nothing.
/// </summary>
internal sealed class JobContractServer : HttpMessageHandler
{
    private readonly ConcurrentDictionary<Guid, string> _states = new();

    public void Add(Guid jobId) => _states[jobId] = "Pending";

    public string State(Guid jobId) => _states[jobId];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        if (path == DeviceApi.HealthPath)
            return Json(new { success = true, tenantName = "QA", deviceName = "Kasa 1", serverTimeUtc = DateTime.UtcNow });

        if (path == DeviceApi.PendingPath)
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

/// <summary>
/// Every log entry at every level, including the HTTP client factory's own, written out as fully as any sink could:
/// the message, each structured property, and the exception with its <see cref="Exception.Data"/>, inner exceptions
/// included.
/// </summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries;

    public string Text() => string.Join(Environment.NewLine, _entries.Select(entry => entry.Text));

    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CategoryLogger(CapturedLogs logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var properties = (state as IEnumerable<KeyValuePair<string, object?>> ?? [])
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Last().Value);

            var text = new StringBuilder($"{logLevel} {category}: {message} {state}");
            foreach (var (key, value) in properties)
                text.Append($" {key}={value}");
            text.Append($" {exception}");
            for (var current = exception; current is not null; current = current.InnerException)
            {
                foreach (DictionaryEntry entry in current.Data)
                    text.Append($" {entry.Key}={entry.Value}");
            }

            logs._entries.Enqueue(new CapturedLogEntry(logLevel, category, message, properties, exception, text.ToString()));
        }
    }
}

internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception,
    string Text);
