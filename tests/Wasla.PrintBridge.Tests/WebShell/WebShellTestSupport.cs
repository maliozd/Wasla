using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>
/// <see cref="PrintBridgeCultureService"/> changes process-wide thread cultures, so tests that use it run
/// alone and restore the original cultures through <see cref="CultureScope"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCultureCollection
{
    public const string Name = "Print Bridge process-wide culture";
}

internal sealed class CultureScope : IDisposable
{
    private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;
    private readonly System.Globalization.CultureInfo _uiCulture = System.Globalization.CultureInfo.CurrentUICulture;
    private readonly System.Globalization.CultureInfo? _defaultCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
    private readonly System.Globalization.CultureInfo? _defaultUiCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        System.Globalization.CultureInfo.CurrentCulture = _culture;
        System.Globalization.CultureInfo.CurrentUICulture = _uiCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }
}

internal static class WebShellTestSupport
{
    public const string ShellDocument = "https://shell.printbridge.invalid/index.html";
    public const string SentinelToken = "pb_live_SENTINEL-TOKEN-0f3a9c";
    public const string SentinelServerUrl = "https://sentinel-tenant.wasla.example";

    public static PrintBridgeCultureService Culture(string name = SupportedCultures.Turkish)
    {
        var service = new PrintBridgeCultureService();
        service.Initialize(name);
        return service;
    }

    public static ShellSnapshotFactory Factory(PrintBridgeCultureService culture) =>
        new(new PrintBridgeLocalizer(culture), culture);

    public static PrintBridgeRuntimeStatus Status(
        BridgeServerConnectionStatus connection = BridgeServerConnectionStatus.Connected,
        PrintBridgeRuntimeIssue? issue = null,
        bool isRunning = true,
        IReadOnlyList<LocalPrintJobRecord>? jobs = null,
        string printerName = "POS-58",
        string displayName = "Kasa 1",
        DateTime? lastContactUtc = null,
        bool dryRun = false,
        DateTime? lastPrintUtc = null,
        PrinterHealthStatus printerHealth = PrinterHealthStatus.Ready) =>
        new()
        {
            IsRunning = isRunning,
            IsConnected = connection == BridgeServerConnectionStatus.Connected,
            LastSuccessfulContactUtc = lastContactUtc ?? new DateTime(2026, 10, 4, 17, 20, 0, DateTimeKind.Utc),
            LastIssue = issue,
            ServerUrl = SentinelServerUrl,
            PrinterName = printerName,
            LocalDeviceName = displayName,
            DisplayName = displayName,
            MachineName = "QA-MACHINE",
            AppVersion = "v1.0.0",
            DryRun = dryRun,
            LastPrintTimeUtc = lastPrintUtc,
            RecentJobs = jobs ?? Array.Empty<LocalPrintJobRecord>(),
            ServerConnectionStatus = connection,
            PrinterHealthStatus = printerHealth
        };

    public static string Command(string type, string payload = "{}") =>
        $$"""{"version":2,"type":"{{type}}","payload":{{payload}}}""";

    /// <summary>A command carrying a fresh request id, plus any extra payload fields (already JSON-encoded).</summary>
    public static string Tracked(string type, string extraFields = "", string? requestId = null) =>
        Command(type, $$"""{"requestId":"{{requestId ?? NewRequestId()}}"{{(extraFields.Length > 0 ? "," + extraFields : string.Empty)}}}""");

    public static string NewRequestId() => Guid.NewGuid().ToString("D");

    public static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Wasla.sln")))
                return current;
            current = Path.GetDirectoryName(current);
        }

        throw new DirectoryNotFoundException("Repository root (Wasla.sln) not found.");
    }

    public static string AssetsDirectory() =>
        Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "WebShell", "Assets");
}

/// <summary>
/// Stands in for <see cref="PrintBridgeRuntime"/>. Status is settable; every engine operation is counted and can be
/// made to fail or to wait on a gate so tests can observe in-flight behaviour.
/// </summary>
internal sealed class FakeStatusSource : IPrintBridgeEngine
{
    private PrintBridgeRuntimeStatus _status = WebShellTestSupport.Status();
    private int _startCalls;
    private int _stopCalls;
    private int _testPrintCalls;
    private int _testConnectionCalls;
    private int _resetCalls;
    private int _reprintCalls;

    public event EventHandler? StatusChanged;

    public int GetStatusCalls { get; private set; }

    public int StartCalls => Volatile.Read(ref _startCalls);
    public int StopCalls => Volatile.Read(ref _stopCalls);
    public int TestPrintCalls => Volatile.Read(ref _testPrintCalls);
    public int TestConnectionCalls => Volatile.Read(ref _testConnectionCalls);
    public int ResetCalls => Volatile.Read(ref _resetCalls);
    public int ReprintCalls => Volatile.Read(ref _reprintCalls);

    public List<Guid> ReprintedJobs { get; } = [];

    public Exception? StartFailure { get; set; }
    public Exception? TestPrintFailure { get; set; }
    public Exception? TestConnectionFailure { get; set; }
    public Exception? ReprintFailure { get; set; }

    /// <summary>The message key the fake server returns for a successful reprint.</summary>
    public string ReprintMessageKey { get; set; } = "Reprint.Created";

    /// <summary>When set, test print waits for this gate before completing.</summary>
    public TaskCompletionSource? TestPrintGate { get; set; }

    /// <summary>When set, stop waits for this gate before completing.</summary>
    public TaskCompletionSource? StopGate { get; set; }

    public Exception? HistoryFailure { get; set; }

    public IReadOnlyList<LocalPrintJobRecord> History { get; set; } = Array.Empty<LocalPrintJobRecord>();

    public (PrintHistoryDateFilter Filter, string? Search)? LastHistoryQuery { get; private set; }

    public bool IsRunning => _status.IsRunning;

    public PrintBridgeRuntimeStatus GetStatus()
    {
        GetStatusCalls++;
        return _status;
    }

    public void Set(PrintBridgeRuntimeStatus status, bool raise = true)
    {
        _status = status;
        if (raise)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Raise() => StatusChanged?.Invoke(this, EventArgs.Empty);

    public int SubscriberCount => StatusChanged?.GetInvocationList().Length ?? 0;

    public void Start()
    {
        Interlocked.Increment(ref _startCalls);
        if (StartFailure is not null)
            throw StartFailure;
        Set(WebShellTestSupport.Status(isRunning: true), raise: true);
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref _stopCalls);
        if (StopGate is { } gate)
            await gate.Task;
        Set(WebShellTestSupport.Status(BridgeServerConnectionStatus.Stopped, isRunning: false), raise: true);
    }

    public Task<WaslaPrintBridgeClient.PrintBridgeHealthResult> TestConnectionAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _testConnectionCalls);
        if (TestConnectionFailure is not null)
            throw TestConnectionFailure;
        return Task.FromResult(new WaslaPrintBridgeClient.PrintBridgeHealthResult("QA", "Kasa 1", DateTime.UtcNow));
    }

    public Task ResetConnectionForReconnectAsync()
    {
        Interlocked.Increment(ref _resetCalls);
        return Task.CompletedTask;
    }

    public async Task TestPrinterAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _testPrintCalls);
        if (TestPrintGate is { } gate)
            await gate.Task.WaitAsync(ct);
        if (TestPrintFailure is not null)
            throw TestPrintFailure;
    }

    public IReadOnlyList<LocalPrintJobRecord> GetPrintHistory(PrintHistoryDateFilter filter, string? orderSearch)
    {
        LastHistoryQuery = (filter, orderSearch);
        if (HistoryFailure is not null)
            throw HistoryFailure;
        return History;
    }

    public Task<WaslaPrintBridgeClient.ReprintJobResult> ReprintJobAsync(Guid jobId, CancellationToken ct)
    {
        Interlocked.Increment(ref _reprintCalls);
        ReprintedJobs.Add(jobId);
        if (ReprintFailure is not null)
            throw ReprintFailure;
        return Task.FromResult(new WaslaPrintBridgeClient.ReprintJobResult(true, ReprintMessageKey, Guid.NewGuid()));
    }
}

internal sealed class FakeNativeActions : IShellNativeActions
{
    public int ClassicWindowRequests { get; private set; }
    public int ConnectionSetupRequests { get; private set; }
    public int ResetConfirmations { get; private set; }
    public int LogFolderRequests { get; private set; }
    public bool ConfirmReset { get; set; } = true;
    public bool LogFolderExists { get; set; } = true;

    public void OpenClassicWindow() => ClassicWindowRequests++;

    public void OpenConnectionSetup() => ConnectionSetupRequests++;

    public bool ConfirmConnectionReset()
    {
        ResetConfirmations++;
        return ConfirmReset;
    }

    public bool OpenLogFolder()
    {
        LogFolderRequests++;
        return LogFolderExists;
    }
}

internal sealed class FakePrinterCatalog : IPrinterCatalog
{
    private int _refreshCalls;

    public List<string> Printers { get; } = ["Microsoft Print to PDF", "POS-58"];

    public IReadOnlyList<string> Installed { get; private set; } = Array.Empty<string>();

    public bool HasDiscovered { get; private set; }

    public int RefreshCalls => Volatile.Read(ref _refreshCalls);

    public TaskCompletionSource? RefreshGate { get; set; }

    public async Task<IReadOnlyList<string>> RefreshAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _refreshCalls);
        if (RefreshGate is { } gate)
            await gate.Task.WaitAsync(ct);
        Installed = Printers.ToArray();
        HasDiscovered = true;
        return Installed;
    }

    public bool IsInstalled(string printerName) =>
        Installed.Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase));
}

internal sealed class FakePrinterSettings : IShellPrinterSettings
{
    public List<string> Saved { get; } = [];

    public string? FailWithKey { get; set; }

    public bool TrySavePrinter(string printerName, out string? errorKey)
    {
        errorKey = FailWithKey;
        if (FailWithKey is not null)
            return false;
        Saved.Add(printerName);
        return true;
    }
}

/// <summary>A complete bridge wired to fakes, as the shell window wires it to the real engine.</summary>
internal sealed class ShellTestRig : IDisposable
{
    public ShellTestRig(PrintBridgeCultureService culture, Wasla.PrintBridge.Services.PrintBridgeSettingsHolder? settings = null)
    {
        Culture = culture;
        Settings = settings ?? NewSettings("test-token-not-a-real-credential");
        var localizer = new PrintBridgeLocalizer(culture);
        Languages = new FakeLanguageSwitcher(culture);
        History = new ShellHistory(Engine, localizer, culture);
        Operations = new ShellOperations(
            Engine,
            Catalog,
            PrinterSettings,
            Native,
            History,
            Settings,
            localizer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TimeSpan.FromSeconds(10));
        Factory = new ShellSnapshotFactory(localizer, culture, Settings, Catalog, () => Operations.Busy, "154.0.4258.53");
        Bridge = new ShellBridge(
            Engine,
            Factory,
            Host,
            Languages,
            culture,
            Operations,
            History,
            Native,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    public PrintBridgeCultureService Culture { get; }
    public Wasla.PrintBridge.Services.PrintBridgeSettingsHolder Settings { get; }
    public FakeStatusSource Engine { get; } = new();
    public FakeShellHost Host { get; } = new();
    public FakeNativeActions Native { get; } = new();
    public FakePrinterCatalog Catalog { get; } = new();
    public FakePrinterSettings PrinterSettings { get; } = new();
    public FakeLanguageSwitcher Languages { get; }
    public ShellHistory History { get; }
    public ShellOperations Operations { get; }
    public ShellSnapshotFactory Factory { get; }
    public ShellBridge Bridge { get; }

    public static Wasla.PrintBridge.Services.PrintBridgeSettingsHolder NewSettings(string token)
    {
        var holder = new Wasla.PrintBridge.Services.PrintBridgeSettingsHolder();
        holder.Replace(
            new Wasla.PrintBridge.Options.WaslaOptions { ServerUrl = WebShellTestSupport.SentinelServerUrl, AgentToken = token },
            new Wasla.PrintBridge.Options.PrintBridgeOptions { PrinterName = "POS-58", DryRun = false },
            new Wasla.PrintBridge.Options.UiOptions());
        return holder;
    }

    /// <summary>Messages of one host message type, parsed.</summary>
    public IReadOnlyList<System.Text.Json.JsonElement> Messages(string type) =>
        Host.Sent
            .Select(s => System.Text.Json.JsonDocument.Parse(s).RootElement)
            .Where(m => m.GetProperty("type").GetString() == type)
            .ToArray();

    public System.Text.Json.JsonElement LastSnapshot() => Messages(ShellMessageContract.SnapshotUpdated)[^1].GetProperty("payload");

    public void Dispose() => Bridge.Dispose();
}

/// <summary>Records what the bridge sends; queued UI actions run only when <see cref="RunQueued"/> is called.</summary>
internal sealed class FakeShellHost : IShellHost
{
    private readonly object _sync = new();
    private readonly Queue<Action> _queue = new();

    public bool IsAvailable { get; set; } = true;

    public List<string> Sent { get; } = [];

    public int PostCalls;

    public void Post(Action action)
    {
        Interlocked.Increment(ref PostCalls);
        lock (_sync)
            _queue.Enqueue(action);
    }

    public int RunQueued()
    {
        var count = 0;
        while (true)
        {
            Action? next;
            lock (_sync)
            {
                if (!_queue.TryDequeue(out next))
                    return count;
            }

            next();
            count++;
        }
    }

    public void PostWebMessageAsJson(string json)
    {
        lock (_sync)
            Sent.Add(json);
    }
}

internal sealed class FakeLanguageSwitcher(PrintBridgeCultureService culture) : IShellLanguageSwitcher
{
    public List<string> Requests { get; } = [];

    public bool Succeed { get; set; } = true;

    public Task<bool> ChangeAsync(string requested)
    {
        Requests.Add(requested);
        if (!Succeed)
            return Task.FromResult(false);

        culture.Initialize(requested);
        return Task.FromResult(true);
    }
}
