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
        DateTime? lastPrintUtc = null) =>
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
            PrinterHealthStatus = PrinterHealthStatus.Ready
        };

    public static string Command(string type, string payload = "{}") =>
        $$"""{"version":1,"type":"{{type}}","payload":{{payload}}}""";

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

internal sealed class FakeStatusSource : IPrintBridgeStatusSource
{
    private PrintBridgeRuntimeStatus _status = WebShellTestSupport.Status();

    public event EventHandler? StatusChanged;

    public int GetStatusCalls { get; private set; }

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
}

/// <summary>Records what the bridge sends; queued UI actions run only when <see cref="RunQueued"/> is called.</summary>
internal sealed class FakeShellHost : IShellHost
{
    private readonly object _sync = new();
    private readonly Queue<Action> _queue = new();

    public bool IsAvailable { get; set; } = true;

    public List<string> Sent { get; } = [];

    public int PostCalls;

    public int ClassicWindowRequests { get; private set; }

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

    public void PostWebMessageAsJson(string json) => Sent.Add(json);

    public void OpenClassicWindow() => ClassicWindowRequests++;
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
