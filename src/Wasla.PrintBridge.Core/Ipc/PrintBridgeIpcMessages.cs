using System.Globalization;
using System.Text.Json;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Ipc;

// Envelopes. Property names are camelCase on the wire; unknown members are rejected (PrintBridgeIpcProtocol.JsonOptions).

public sealed record IpcHello(string Type, int Protocol, string Client);

public sealed record IpcWelcome(string Type, int Protocol, string Service);

public sealed record IpcHandshakeError(string Type, string Code, int Min, int Max);

public sealed record IpcRequest(string Type, string Id, string Command, JsonElement Payload);

public sealed record IpcResponse(string Type, string Id, bool Ok, JsonElement? Payload, IpcError? Error);

public sealed record IpcEvent(string Type, string Event, JsonElement Payload);

/// <param name="Code">One of the <c>Error*</c> codes of <see cref="PrintBridgeIpcProtocol"/>.</param>
/// <param name="Key">A resource key the app localizes; never free text.</param>
/// <param name="Args">Format arguments of <paramref name="Key"/> (numbers as invariant text), at most four short values.</param>
/// <param name="Issue">For <c>connection</c> and <c>not_active</c>: the <see cref="PrintBridgeRuntimeIssueCode"/>.</param>
/// <param name="Status">For <c>connection</c>: the HTTP status the server answered, if any.</param>
public sealed record IpcError(string Code, string? Key, string[]? Args, PrintBridgeRuntimeIssueCode? Issue, int? Status);

/// <summary>What the service can do right now.</summary>
public enum IpcAvailability
{
    /// <summary>Started, but has not decided yet (checking the folder, taking the engine lock).</summary>
    Starting,

    Active,

    /// <summary>Another engine holds the machine-wide engine lock; the service waits for it.</summary>
    EngineHeldElsewhere,

    /// <summary>The data folder is not protected; the engine is not started.</summary>
    DataFolderNotSecured
}

// State (service → app).

public sealed record IpcState(IpcAvailability Availability, IpcStatus? Status, IpcSettings? Settings);

public sealed record IpcIssue(PrintBridgeRuntimeIssueCode Code, string? Key, string[] Args);

public sealed record IpcJob(
    Guid JobId,
    Guid OrderId,
    string? OrderDisplay,
    string? Platform,
    string? JobType,
    string? PrinterName,
    LocalPrintJobStatus Status,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? PrintedAtUtc,
    string? StatusNote,
    LocalPrintJobFailureCategory? FailureCategory);

public sealed record IpcStatus(
    bool IsRunning,
    bool IsConnected,
    DateTime? LastSuccessfulContactUtc,
    DateTime? LastPollUtc,
    IpcIssue? LastIssue,
    string PrinterName,
    string DisplayName,
    bool ServerDeviceNameResolved,
    string AppVersion,
    bool DryRun,
    IpcJob[] RecentJobs,
    int JobsTodayCount,
    int FailedTodayCount,
    DateTime? LastPrintTimeUtc,
    BridgeServerConnectionStatus ServerConnectionStatus,
    PrinterHealthStatus PrinterHealthStatus,
    TrayIconState TrayIconState,
    int UncertainJobsCount);

/// <summary>The saved settings without secrets: the token is only <see cref="HasSavedToken"/>.</summary>
public sealed record IpcSettings(
    string ServerUrl,
    bool HasSavedToken,
    string PrinterMode,
    string PrinterName,
    bool DryRun,
    int IdlePollSeconds,
    int BusyPollSeconds,
    int ErrorPollSeconds,
    int MaxJobsPerPoll,
    string DisplayName,
    bool ServerDeviceNameResolved,
    string Language,
    bool PrinterUsable,
    bool PrintingStoppedByUser);

// Request payloads (app → service). Each validates itself; anything out of range is malformed.

public sealed record IpcEmptyPayload;

public sealed record IpcConnectionCheckPayload(string ServerUrl, string Token, bool KeepSavedToken);

public sealed record IpcConnectionApplyPayload(string VerificationId, bool StartListening);

public sealed record IpcPrinterSavePayload(string Name);

public sealed record IpcSettingsSavePayload(bool TestMode, int IdlePollSeconds, int BusyPollSeconds, int ErrorPollSeconds);

public sealed record IpcLanguageSavePayload(string Culture);

public sealed record IpcHistoryQueryPayload(string Range, string? Search, int Page, int PageSize);

public sealed record IpcHistoryReprintPayload(Guid JobId);

public sealed record IpcHistoryResolvePayload(Guid JobId, bool Printed);

public sealed record IpcSetupApplyPayload(string Link);

public sealed record IpcSetupConsentAnswerPayload(string ConsentId, bool Approved);

// Response payloads (service → app).

public sealed record IpcHealth(string TenantName, string DeviceName, DateTime ServerTimeUtc);

public sealed record IpcConnectionCheckResult(string VerificationId, IpcHealth Health);

public sealed record IpcConnectionApplyResult(PrintBridgeConnectionChange Change);

public sealed record IpcPrintersResult(string[] Printers);

public sealed record IpcErrorKeyResult(string? ErrorKey);

public sealed record IpcHistoryResult(int Total, int Page, IpcJob[] Rows);

public sealed record IpcReprintResult(bool Success, string MessageKey, Guid? NewPrintJobId);

public sealed record IpcSetupApplyResult(Setup.PrintBridgeAutoSetupOutcome Outcome);

// Event payloads (service → app).

public sealed record IpcSetupConsentEvent(
    string ConsentId,
    string CurrentTenantName,
    string CurrentServer,
    string TargetTenantName,
    string TargetServer);

public sealed record IpcSetupBackgroundEvent(Setup.PrintBridgeAutoSetupOutcome Outcome);

/// <summary>Validation of request payloads and the mappings between engine types and wire types.</summary>
public static class PrintBridgeIpcMapping
{
    public const int MaxServerUrlLength = 2048;
    public const int MaxTokenLength = 512;
    public const int MaxPrinterNameLength = 256;
    public const int MaxSearchLength = 64;
    public const int MaxLinkLength = 8 * 1024;
    public const int MaxHistoryPage = 1000;
    public const int MaxPollSecondsOnWire = 86400;
    public const int MaxArgs = 4;
    public const int MaxArgLength = 128;

    public static readonly IReadOnlyDictionary<string, PrintHistoryDateFilter> HistoryRanges =
        new Dictionary<string, PrintHistoryDateFilter>(StringComparer.Ordinal)
        {
            ["today"] = PrintHistoryDateFilter.Today,
            ["last7Days"] = PrintHistoryDateFilter.Last7Days,
            ["last30Days"] = PrintHistoryDateFilter.Last30Days
        };

    public static readonly IReadOnlySet<string> Cultures = new HashSet<string>(StringComparer.Ordinal) { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    /// <summary>Returns the payload when every field is present and in range; otherwise throws a malformed error.</summary>
    public static T Validate<T>(T payload)
    {
        var valid = payload switch
        {
            IpcEmptyPayload => true,
            IpcConnectionCheckPayload p => p.ServerUrl is { Length: <= MaxServerUrlLength }
                                           && p.Token is { Length: <= MaxTokenLength }
                                           && (!p.KeepSavedToken || p.Token.Length == 0),
            IpcConnectionApplyPayload p => p.VerificationId is not null && PrintBridgeIpcProtocol.OpaqueIdPattern().IsMatch(p.VerificationId),
            IpcPrinterSavePayload p => IsPrinterName(p.Name),
            IpcSettingsSavePayload p => p.IdlePollSeconds is >= 0 and <= MaxPollSecondsOnWire
                                        && p.BusyPollSeconds is >= 0 and <= MaxPollSecondsOnWire
                                        && p.ErrorPollSeconds is >= 0 and <= MaxPollSecondsOnWire,
            IpcLanguageSavePayload p => p.Culture is not null && Cultures.Contains(p.Culture),
            IpcHistoryQueryPayload p => p.Range is not null && HistoryRanges.ContainsKey(p.Range)
                                        && (p.Search is null || (p.Search.Length <= MaxSearchLength && !HasControlCharacters(p.Search)))
                                        && p.Page is >= 0 and <= MaxHistoryPage
                                        && p.PageSize is >= 1 and <= Hosting.PrintBridgeEngineHost.MaxHistoryPageSize,
            IpcHistoryReprintPayload p => p.JobId != Guid.Empty,
            IpcHistoryResolvePayload p => p.JobId != Guid.Empty,
            IpcSetupApplyPayload p => p.Link is { Length: > 0 and <= MaxLinkLength }
                                      && p.Link.StartsWith("wasla-printbridge://", StringComparison.OrdinalIgnoreCase)
                                      && !HasControlCharacters(p.Link),
            IpcSetupConsentAnswerPayload p => p.ConsentId is not null && PrintBridgeIpcProtocol.OpaqueIdPattern().IsMatch(p.ConsentId),
            _ => false
        };

        return valid ? payload : throw new PrintBridgeIpcProtocolException(PrintBridgeIpcProtocol.ErrorMalformed);
    }

    public static bool IsPrinterName(string? name) =>
        name is { Length: > 0 and <= MaxPrinterNameLength }
        && name.Trim().Length == name.Length
        && !HasControlCharacters(name);

    private static bool HasControlCharacters(string value) => value.Any(char.IsControl);

    public static IpcIssue ToWire(PrintBridgeRuntimeIssue issue) => new(issue.Code, issue.ResourceKey, Args(issue.Args));

    public static PrintBridgeRuntimeIssue FromWire(IpcIssue issue) =>
        new(issue.Code, issue.Key, issue.Args.Cast<object>().ToArray());

    /// <summary>Format arguments as short invariant text: never an object's ToString of unknown content.</summary>
    public static string[] Args(object[]? args) =>
        (args ?? [])
            .Take(MaxArgs)
            .Select(a => a switch
            {
                int or long or short or byte or decimal or double or float => Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty,
                string s => s.Length <= MaxArgLength ? s : s[..MaxArgLength],
                _ => string.Empty
            })
            .ToArray();

    public static IpcJob ToWire(LocalPrintJobRecord job)
    {
        var safe = LocalPrintJobFailure.ForClient(job);
        return new IpcJob(
            safe.JobId,
            safe.OrderId,
            safe.OrderDisplay,
            safe.Platform,
            safe.JobType,
            safe.PrinterName,
            safe.Status,
            safe.CreatedAtUtc,
            safe.LastAttemptAtUtc,
            safe.PrintedAtUtc,
            safe.StatusNote,
            safe.FailureCategory);
    }

    public static LocalPrintJobRecord FromWire(IpcJob job) =>
        new()
        {
            JobId = job.JobId,
            OrderId = job.OrderId,
            OrderDisplay = job.OrderDisplay,
            Platform = job.Platform,
            JobType = job.JobType,
            PrinterName = job.PrinterName,
            Status = job.Status,
            CreatedAtUtc = job.CreatedAtUtc,
            LastAttemptAtUtc = job.LastAttemptAtUtc,
            PrintedAtUtc = job.PrintedAtUtc,
            StatusNote = job.StatusNote,
            FailureCategory = job.FailureCategory
        };

    public static IpcStatus ToWire(PrintBridgeRuntimeStatus status) =>
        new(
            status.IsRunning,
            status.IsConnected,
            status.LastSuccessfulContactUtc,
            status.LastPollUtc,
            status.LastIssue is null ? null : ToWire(status.LastIssue),
            status.PrinterName ?? string.Empty,
            status.DisplayName ?? string.Empty,
            status.ServerDeviceNameResolved,
            status.AppVersion ?? string.Empty,
            status.DryRun,
            status.RecentJobs.Select(ToWire).ToArray(),
            status.JobsTodayCount,
            status.FailedTodayCount,
            status.LastPrintTimeUtc,
            status.ServerConnectionStatus,
            status.PrinterHealthStatus,
            status.TrayIconState,
            status.UncertainJobsCount);

    public static PrintBridgeRuntimeStatus FromWire(IpcStatus status)
    {
        var issue = status.LastIssue is null ? null : FromWire(status.LastIssue);
        return new PrintBridgeRuntimeStatus
        {
            IsRunning = status.IsRunning,
            IsConnected = status.IsConnected,
            LastSuccessfulContactUtc = status.LastSuccessfulContactUtc,
            LastPollUtc = status.LastPollUtc,
            LastIssue = issue,
            LastError = issue?.EffectiveResourceKey,
            PrinterName = status.PrinterName,
            LocalDeviceName = status.DisplayName,
            DisplayName = status.DisplayName,
            ServerDeviceNameResolved = status.ServerDeviceNameResolved,
            AppVersion = status.AppVersion,
            DryRun = status.DryRun,
            RecentJobs = status.RecentJobs.Select(FromWire).ToArray(),
            JobsTodayCount = status.JobsTodayCount,
            FailedTodayCount = status.FailedTodayCount,
            LastPrintTimeUtc = status.LastPrintTimeUtc,
            ServerConnectionStatus = status.ServerConnectionStatus,
            PrinterHealthStatus = status.PrinterHealthStatus,
            TrayIconState = status.TrayIconState,
            UncertainJobsCount = status.UncertainJobsCount
        };
    }

    public static IpcSettings ToWire(IPrintBridgeSettingsView settings)
    {
        var bridge = settings.Bridge;
        return new IpcSettings(
            settings.ServerUrl ?? string.Empty,
            settings.HasSavedToken,
            bridge.PrinterMode ?? string.Empty,
            bridge.PrinterName ?? string.Empty,
            bridge.DryRun,
            bridge.IdlePollIntervalSeconds,
            bridge.BusyPollIntervalSeconds,
            bridge.ErrorPollIntervalSeconds,
            bridge.MaxJobsPerPoll,
            bridge.DisplayName ?? string.Empty,
            bridge.ServerDeviceNameResolved,
            settings.Ui.Language ?? string.Empty,
            settings.IsPrinterUsable,
            bridge.PrintingStoppedByUser);
    }

    public static IpcHealth ToWire(WaslaPrintBridgeClient.PrintBridgeHealthResult health) =>
        new(health.TenantName ?? string.Empty, health.DeviceName ?? string.Empty, health.ServerTimeUtc);

    public static WaslaPrintBridgeClient.PrintBridgeHealthResult FromWire(IpcHealth health) =>
        new(health.TenantName, health.DeviceName, health.ServerTimeUtc);
}
