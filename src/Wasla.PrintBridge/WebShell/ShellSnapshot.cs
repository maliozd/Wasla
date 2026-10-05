using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Everything the page may show, already localized and formatted by the host. The page renders it verbatim and
/// never derives state of its own. It intentionally has no field for the server URL, device token, installation
/// id, machine name or any other credential or tenant identifier.
/// </summary>
public sealed record ShellSnapshot(
    string Culture,
    string Direction,
    ShellConnectionView Connection,
    ShellEngineView Engine,
    ShellDeviceView Device,
    ShellPrinterView Printer,
    ShellActivityView Activity,
    ShellJobView? LastJob,
    ShellActionsView Actions,
    ShellBusyState Busy,
    ShellDiagnosticsView Diagnostics,
    bool DryRun,
    ShellOperationalView Operational,
    IReadOnlyList<ShellLanguageOption> Languages,
    IReadOnlyDictionary<string, string> Strings);

/// <summary>
/// The saved operational settings, which are also the values the engine uses (it reads them for every poll and job),
/// with the ranges the host accepts. The page edits a copy and saves it with <c>settings.save</c>.
/// </summary>
public sealed record ShellOperationalView(
    bool TestMode,
    ShellSecondsSetting IdlePoll,
    ShellSecondsSetting BusyPoll,
    ShellSecondsSetting ErrorPoll);

/// <param name="RangeLabel">The allowed range, localized and formatted by the host (for example "1–300 s").</param>
public sealed record ShellSecondsSetting(int Value, int Min, int Max, string RangeLabel);

/// <param name="State">One of <see cref="ShellConnectionStates"/>.</param>
public sealed record ShellConnectionView(string State, string Label, string Detail);

/// <param name="State"><c>running</c> or <c>stopped</c>.</param>
public sealed record ShellEngineView(string State, string Label);

public sealed record ShellDeviceView(string Name);

/// <param name="State"><c>ready</c>, <c>notConfigured</c>, <c>notFound</c> or <c>dryRun</c>.</param>
/// <param name="Installed">Printers the host last discovered on this computer (names only).</param>
public sealed record ShellPrinterView(string? Name, string State, string Label, IReadOnlyList<string> Installed);

/// <param name="LastContact">Formatted time of the last successful server contact, or null.</param>
/// <param name="LastPrint">Formatted time of the last successful print, or null.</param>
public sealed record ShellActivityView(string? LastContact, string? LastPrint, int JobsToday, int FailedToday);

/// <param name="Status"><c>pending</c>, <c>printing</c>, <c>printed</c>, <c>failed</c> or <c>skipped</c>.</param>
public sealed record ShellJobView(string Status, string StatusLabel, string Order, string TypeLabel, string Time);

/// <summary>Which actions make sense right now, decided by the host from engine and settings state.</summary>
public sealed record ShellActionsView(
    bool Start,
    bool Stop,
    bool TestPrint,
    bool CheckConnection,
    bool Reconnect,
    bool ConfigurePrinter,
    bool ResetConnection);

/// <summary>Sanitized support information. No paths, tokens, server addresses or machine names.</summary>
public sealed record ShellDiagnosticsView(
    string AppVersion,
    string WebView2Version,
    string EngineLabel,
    string? LastContact,
    string? LastIssue,
    string PrinterName,
    string PrinterLabel,
    string TestModeLabel);

public sealed record ShellLanguageOption(string Culture, string NativeName);

public static class ShellConnectionStates
{
    public const string Online = "online";
    public const string Connecting = "connecting";
    public const string Offline = "offline";
    public const string Error = "error";
    public const string NotConfigured = "notConfigured";
    public const string Stopped = "stopped";

    public static readonly IReadOnlyList<string> All = [Online, Connecting, Offline, Error, NotConfigured, Stopped];
}

/// <summary>Host-to-page envelope. <see cref="Sequence"/> increases with every message so the page can drop stale ones.</summary>
public sealed record ShellHostMessage<TPayload>(int Version, string Type, long Sequence, TPayload Payload);

/// <summary>Reply to <c>history.query</c>; <see cref="RequestId"/> lets the page ignore replies to older queries.</summary>
public sealed record ShellHistoryResult(string RequestId, ShellHistoryPage History);

/// <summary>Reply to a state-changing command.</summary>
public sealed record ShellOperationResultView(string? RequestId, string Operation, string Outcome, string Message);

/// <summary>Asks the page to show one of <see cref="ShellMessageContract.Tabs"/>.</summary>
public sealed record ShellNavigateView(string Tab);

public static class ShellMessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string SerializePayload(ShellSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static string SerializeSnapshotMessage(ShellSnapshot snapshot, long sequence) =>
        Serialize(ShellMessageContract.SnapshotUpdated, sequence, snapshot);

    public static string SerializeOperationResult(ShellOperationResult result, long sequence) =>
        Serialize(
            ShellMessageContract.OperationResult,
            sequence,
            new ShellOperationResultView(
                result.RequestId,
                result.Operation,
                JsonNamingPolicy.CamelCase.ConvertName(result.Outcome.ToString()),
                result.Message));

    public static string SerializeHistoryResult(ShellHistoryResult result, long sequence) =>
        Serialize(ShellMessageContract.HistoryResult, sequence, result);

    public static string SerializeNavigate(string tab, long sequence) =>
        Serialize(ShellMessageContract.UiNavigate, sequence, new ShellNavigateView(tab));

    private static string Serialize<TPayload>(string type, long sequence, TPayload payload) =>
        JsonSerializer.Serialize(new ShellHostMessage<TPayload>(ShellMessageContract.Version, type, sequence, payload), Options);
}
