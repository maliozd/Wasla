using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.WebShell;

/// <summary>
/// Projects engine, settings and host operation state into the page model, using the existing Print Bridge
/// resources and date formats so the shell and the classic window say the same thing.
/// </summary>
public sealed class ShellSnapshotFactory
{
    /// <summary>Static page labels. Only keys without format placeholders; formatted values travel as fields.</summary>
    public static readonly IReadOnlyList<string> StringKeys =
    [
        "Common.AppTitle",
        "Common.Dash",
        "Settings.Language",
        "Shell.Nav.Label",
        "Shell.Tab.Overview",
        "Shell.Tab.Printer",
        "Tab.PrintHistory",
        "Tab.Settings",
        "Dashboard.ServerStatus",
        "Dashboard.LastContact",
        "Dashboard.LastPrint",
        "Dashboard.JobsToday",
        "Dashboard.FailedToday",
        "Dashboard.PrinterStatus",
        "Shell.Tile.Printer",
        "Shell.Tile.Engine",
        "Shell.LastJob.Title",
        "Shell.Today.Title",
        "Shell.Overview.Actions",
        "RecentJobs.Empty",
        "Settings.DryRunEnabledWarning",
        "Button.StartListening",
        "Button.StopListening",
        "Button.TestPrinter",
        "Button.TestConnection",
        "Button.Refresh",
        "Button.SavePrinter",
        "Button.OpenLogsFolder",
        "Button.ResetConnection",
        "Reprint.Button",
        "Shell.Action.Reconnect",
        "Shell.Action.ConfigurePrinter",
        "Shell.Action.ShowDiagnostics",
        "Shell.Action.OpenSetup",
        "Shell.Action.Working",
        "Shell.Action.Confirm",
        "Shell.Action.Cancel",
        "Shell.Printer.Title",
        "Shell.Printer.Help",
        "Shell.Printer.Placeholder",
        "Shell.Printer.NoneInstalled",
        "Shell.Printer.SavedNotInstalled",
        "Shell.Printer.TestHelp",
        "Settings.PrinterName",
        "Shell.History.Range",
        "PrintHistory.Filter.Today",
        "PrintHistory.Filter.Last7Days",
        "PrintHistory.Filter.Last30Days",
        "PrintHistory.Search",
        "PrintHistory.SearchPlaceholder",
        "PrintHistory.Column.Time",
        "PrintHistory.Column.Order",
        "PrintHistory.Column.Platform",
        "PrintHistory.Column.Printer",
        "PrintHistory.Column.Status",
        "PrintHistory.Empty",
        "PrintHistory.EmptyFiltered",
        "Shell.History.ConfirmReprint",
        "Shell.History.Previous",
        "Shell.History.Next",
        "Shell.History.Loading",
        "Shell.Settings.Connection",
        "Shell.Settings.ConnectionHelp",
        "Settings.ResetConnectionHelp",
        "Shell.Settings.Appearance",
        "Shell.Settings.ThemeFollowsWindows",
        "Shell.Settings.TestMode",
        "Shell.Settings.AdvancedHelp",
        "Shell.Diagnostics.Title",
        "Shell.Diagnostics.AppVersion",
        "Shell.Diagnostics.WebView2",
        "Shell.Diagnostics.Engine",
        "Shell.Diagnostics.LastError",
        "Shell.Diagnostics.None",
        "Shell.Diagnostics.Fallback",
        "Shell.Diagnostics.FallbackHelp",
        "Shell.OpenClassicWindow",
        "Shell.Toast.Dismiss"
    ];

    /// <summary>Resource keys this projection may emit as labels or details, in addition to <see cref="StringKeys"/>.</summary>
    public static readonly IReadOnlyList<string> StateKeys =
    [
        "Status.Online",
        "Status.Offline",
        "Status.Running",
        "Status.Stopped",
        "Shell.Connection.Connecting",
        "ConnectionStatus.Error",
        "ConnectionStatus.NotConfigured",
        "ConnectionStatus.Stopped",
        "Shell.Detail.Online",
        "Shell.Detail.Connecting",
        "Shell.Detail.Stopped",
        "Shell.Detail.NotConfigured",
        "Shell.Detail.UnexpectedError",
        "Shell.Settings.TestModeOn",
        "Shell.Settings.TestModeOff",
        "Shell.Diagnostics.Unavailable",
        "Shell.Device.Unknown"
    ];

    private readonly PrintBridgeLocalizer _localizer;
    private readonly PrintBridgeCultureService _cultureService;
    private readonly PrintBridgeSettingsHolder? _settings;
    private readonly IPrinterCatalog? _printers;
    private readonly Func<ShellBusyState> _busy;
    private readonly string? _webView2Version;

    public ShellSnapshotFactory(
        PrintBridgeLocalizer localizer,
        PrintBridgeCultureService cultureService,
        PrintBridgeSettingsHolder? settings = null,
        IPrinterCatalog? printers = null,
        Func<ShellBusyState>? busy = null,
        string? webView2Version = null)
    {
        _localizer = localizer;
        _cultureService = cultureService;
        _settings = settings;
        _printers = printers;
        _busy = busy ?? (() => ShellBusyState.Idle);
        _webView2Version = webView2Version;
    }

    public ShellSnapshot Create(PrintBridgeRuntimeStatus status)
    {
        var culture = _cultureService.CurrentCulture;
        var connection = CreateConnection(status);
        var engine = new ShellEngineView(
            status.IsRunning ? "running" : "stopped",
            _localizer[status.IsRunning ? "Status.Running" : "Status.Stopped"]);
        var printerName = string.IsNullOrWhiteSpace(status.PrinterName) ? null : status.PrinterName.Trim();
        var printerLabel = _localizer.GetPrinterHealthStatus(status.PrinterHealthStatus);
        var lastContact = FormatUtc(status.LastSuccessfulContactUtc);

        return new ShellSnapshot(
            Culture: culture.Name,
            Direction: _cultureService.IsRightToLeft ? "rtl" : "ltr",
            Connection: connection,
            Engine: engine,
            // The server-assigned device name only; the Windows machine name is never sent to the page.
            Device: new ShellDeviceView(string.IsNullOrWhiteSpace(status.DisplayName)
                ? _localizer["Shell.Device.Unknown"]
                : status.DisplayName.Trim()),
            Printer: new ShellPrinterView(
                printerName,
                ToPrinterState(status.PrinterHealthStatus),
                printerLabel,
                _printers?.Installed ?? Array.Empty<string>()),
            Activity: new ShellActivityView(
                lastContact,
                FormatUtc(status.LastPrintTimeUtc),
                status.JobsTodayCount,
                status.FailedTodayCount),
            LastJob: CreateLastJob(status.RecentJobs),
            Actions: CreateActions(status, connection.State),
            Busy: _busy(),
            Diagnostics: new ShellDiagnosticsView(
                AppVersion: status.AppVersion,
                WebView2Version: string.IsNullOrWhiteSpace(_webView2Version) ? _localizer["Shell.Diagnostics.Unavailable"] : _webView2Version,
                EngineLabel: engine.Label,
                LastContact: lastContact,
                LastIssue: status.LastIssue is null ? null : DescribeIssue(status.LastIssue),
                PrinterName: printerName ?? _localizer["Common.Dash"],
                PrinterLabel: printerLabel,
                TestModeLabel: _localizer[status.DryRun ? "Shell.Settings.TestModeOn" : "Shell.Settings.TestModeOff"]),
            DryRun: status.DryRun,
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

    /// <summary>
    /// The actions the page may offer. Start is withheld while a reconnect is required (the same gate as the
    /// classic window); test print needs a ready printer outside test mode.
    /// </summary>
    internal ShellActionsView CreateActions(PrintBridgeRuntimeStatus status, string connectionState)
    {
        var token = _settings?.OrderHub.AgentToken;
        var hasToken = !string.IsNullOrWhiteSpace(token);
        var requiresReconnect = PrintBridgePollingGate.RequiresReconnectBeforeStart(token, status.LastIssue)
                                || status.LastIssue?.Code == PrintBridgeRuntimeIssueCode.DuplicateInstallation
                                || connectionState == ShellConnectionStates.NotConfigured;
        var configured = connectionState != ShellConnectionStates.NotConfigured;

        return new ShellActionsView(
            Start: !status.IsRunning && !requiresReconnect,
            Stop: status.IsRunning,
            TestPrint: status.PrinterHealthStatus == PrinterHealthStatus.Ready,
            CheckConnection: configured && !requiresReconnect,
            Reconnect: requiresReconnect,
            ConfigurePrinter: status.PrinterHealthStatus is PrinterHealthStatus.NotConfigured or PrinterHealthStatus.NotFound,
            ResetConnection: hasToken);
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
            Status: ShellHistory.ToStatus(job.Status),
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
