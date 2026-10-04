using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Projects the engine's <see cref="PrintBridgeRuntimeStatus"/> into the page model, using the existing
/// Print Bridge resources and date formats so the shell and the classic window say the same thing.
/// </summary>
public sealed class ShellSnapshotFactory
{
    /// <summary>Static page labels. Only keys without format placeholders; formatted values travel as fields.</summary>
    public static readonly IReadOnlyList<string> StringKeys =
    [
        "Common.AppTitle",
        "Common.Subtitle",
        "Common.Dash",
        "Dashboard.ServerStatus",
        "Dashboard.DeviceName",
        "Dashboard.PrinterStatus",
        "Dashboard.LastContact",
        "Dashboard.LastPrint",
        "Dashboard.JobsToday",
        "Dashboard.FailedToday",
        "Settings.Language",
        "Settings.DryRunEnabledWarning",
        "RecentJobs.Empty",
        "Shell.LastJob.Title",
        "Shell.Today.Title",
        "Shell.OpenClassicWindow",
        "Shell.Preview.Notice"
    ];

    /// <summary>Resource keys this projection may emit as labels or details, in addition to <see cref="StringKeys"/>.</summary>
    public static readonly IReadOnlyList<string> StateKeys =
    [
        "Status.Online",
        "Status.Offline",
        "Shell.Connection.Connecting",
        "ConnectionStatus.Error",
        "ConnectionStatus.NotConfigured",
        "ConnectionStatus.Stopped",
        "Shell.Detail.Online",
        "Shell.Detail.Connecting",
        "Shell.Detail.Stopped",
        "Shell.Detail.NotConfigured",
        "Shell.Detail.UnexpectedError",
        "Footer.Version"
    ];

    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;

    public ShellSnapshotFactory(PrintBridgeLocalizer localizer, PrintBridgeCultureService cultureService)
    {
        _localizer = localizer;
        _cultureService = cultureService;
    }

    public ShellSnapshot Create(PrintBridgeRuntimeStatus status)
    {
        var culture = _cultureService.CurrentCulture;

        return new ShellSnapshot(
            Culture: culture.Name,
            Direction: _cultureService.IsRightToLeft ? "rtl" : "ltr",
            Connection: CreateConnection(status),
            Device: new ShellDeviceView(_localizer.GetFooterDeviceName(status)),
            Printer: new ShellPrinterView(
                string.IsNullOrWhiteSpace(status.PrinterName) ? null : status.PrinterName.Trim(),
                ToPrinterState(status.PrinterHealthStatus),
                _localizer.GetPrinterHealthStatus(status.PrinterHealthStatus)),
            Activity: new ShellActivityView(
                FormatUtc(status.LastSuccessfulContactUtc),
                FormatUtc(status.LastPrintTimeUtc),
                status.JobsTodayCount,
                status.FailedTodayCount),
            LastJob: CreateLastJob(status.RecentJobs),
            DryRun: status.DryRun,
            VersionLabel: _localizer.GetString("Footer.Version", status.AppVersion),
            Languages: SupportedCultures.All
                .Select(c => new ShellLanguageOption(c, SupportedCultures.GetNativeName(c)))
                .ToArray(),
            Strings: StringKeys.ToDictionary(key => key, key => _localizer[key], StringComparer.Ordinal));
    }

    /// <summary>
    /// Maps engine state to the page's connection state. Blocking lifecycle issues (reconnect required,
    /// disabled by admin, duplicate installation) are errors; an unreachable server is offline.
    /// </summary>
    public static string MapConnectionState(PrintBridgeRuntimeStatus status)
    {
        if (status.LastIssue is { IsBlockingLifecycleIssue: true })
            return ShellConnectionStates.Error;

        return status.ServerConnectionStatus switch
        {
            BridgeServerConnectionStatus.NotConfigured => ShellConnectionStates.NotConfigured,
            BridgeServerConnectionStatus.Connected => ShellConnectionStates.Online,
            BridgeServerConnectionStatus.Disconnected => ShellConnectionStates.Connecting,
            BridgeServerConnectionStatus.Stopped => ShellConnectionStates.Stopped,
            BridgeServerConnectionStatus.Error when status.LastIssue?.Code == PrintBridgeRuntimeIssueCode.ServerUnreachable
                => ShellConnectionStates.Offline,
            _ => ShellConnectionStates.Error
        };
    }

    private ShellConnectionView CreateConnection(PrintBridgeRuntimeStatus status)
    {
        var state = MapConnectionState(status);
        var issue = status.LastIssue;

        var label = state switch
        {
            ShellConnectionStates.Online => _localizer["Status.Online"],
            ShellConnectionStates.Connecting => _localizer["Shell.Connection.Connecting"],
            ShellConnectionStates.Offline => _localizer["Status.Offline"],
            ShellConnectionStates.NotConfigured => _localizer["ConnectionStatus.NotConfigured"],
            ShellConnectionStates.Stopped => _localizer["ConnectionStatus.Stopped"],
            _ when issue is { IsBlockingLifecycleIssue: true } => _localizer.GetRuntimeIssueTitle(issue),
            _ => _localizer["ConnectionStatus.Error"]
        };

        var detail = state switch
        {
            ShellConnectionStates.Online => _localizer["Shell.Detail.Online"],
            ShellConnectionStates.Connecting => _localizer["Shell.Detail.Connecting"],
            ShellConnectionStates.NotConfigured => _localizer["Shell.Detail.NotConfigured"],
            ShellConnectionStates.Stopped => _localizer["Shell.Detail.Stopped"],
            _ => DescribeIssue(issue)
        };

        return new ShellConnectionView(state, label, detail);
    }

    // Raw exception text is never shown: it is technical and may echo request details.
    private string DescribeIssue(PrintBridgeRuntimeIssue? issue) =>
        issue is null || issue.Code == PrintBridgeRuntimeIssueCode.Unexpected
            ? _localizer["Shell.Detail.UnexpectedError"]
            : _localizer.GetRuntimeIssueDetail(issue);

    private ShellJobView? CreateLastJob(IReadOnlyList<LocalPrintJobRecord> recentJobs)
    {
        var job = recentJobs.Count > 0 ? recentJobs[0] : null;
        if (job is null)
            return null;

        return new ShellJobView(
            Status: job.Status switch
            {
                LocalPrintJobStatus.Received => "pending",
                LocalPrintJobStatus.Printing => "printing",
                LocalPrintJobStatus.Printed => "printed",
                LocalPrintJobStatus.Failed => "failed",
                _ => "skipped"
            },
            StatusLabel: _localizer.GetJobStatusBadge(job.Status),
            Order: string.IsNullOrWhiteSpace(job.OrderDisplay) ? job.ShortJobId : job.OrderDisplay.Trim(),
            TypeLabel: _localizer.GetJobType(job.JobType),
            Time: FormatUtc(job.DisplayTimeUtc) ?? _localizer["Common.Dash"]);
    }

    private static string ToPrinterState(PrinterHealthStatus status) => status switch
    {
        PrinterHealthStatus.Ready => "ready",
        PrinterHealthStatus.NotFound => "notFound",
        PrinterHealthStatus.DryRun => "dryRun",
        _ => "notConfigured"
    };

    private string? FormatUtc(DateTime? utc) =>
        utc.HasValue
            ? PrintBridgeDateTimeFormatter.FormatDashboardUtc(_cultureService.CurrentCulture, utc.Value)
            : null;
}
