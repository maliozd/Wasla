using System.Globalization;
using System.Reflection;
using System.Resources;
using OrderHub.PrintBridge.Configuration;
using OrderHub.PrintBridge.Models;

namespace OrderHub.PrintBridge.Localization;

public sealed class PrintBridgeLocalizer
{
    private static readonly ResourceManager ResourceManager = new(
        "OrderHub.PrintBridge.Resources.PrintBridgeResources",
        Assembly.GetExecutingAssembly());

    private readonly PrintBridgeCultureService _cultureService;

    public PrintBridgeLocalizer(PrintBridgeCultureService cultureService) =>
        _cultureService = cultureService;

    public string this[string key] => GetString(key);

    public string GetString(string key, params object[] args)
    {
        var format = ResourceManager.GetString(key, _cultureService.CurrentCulture)
            ?? ResourceManager.GetString(key, CultureInfo.GetCultureInfo(SupportedCultures.Default))
            ?? key;

        return args.Length > 0
            ? string.Format(_cultureService.CurrentCulture, format, args)
            : format;
    }

    public string GetJobType(string? jobType)
    {
        if (string.IsNullOrWhiteSpace(jobType))
            return GetString("Common.Dash");

        var key = $"JobType.{jobType}";
        var localized = ResourceManager.GetString(key, _cultureService.CurrentCulture)
            ?? ResourceManager.GetString(key, CultureInfo.GetCultureInfo(SupportedCultures.Default));
        return localized ?? jobType;
    }

    public string GetJobStatus(LocalPrintJobStatus status) => status switch
    {
        LocalPrintJobStatus.Received => GetString("JobStatus.Pending"),
        LocalPrintJobStatus.Printing => GetString("JobStatus.Printing"),
        LocalPrintJobStatus.Printed => GetString("JobStatus.Printed"),
        LocalPrintJobStatus.Failed => GetString("JobStatus.Failed"),
        LocalPrintJobStatus.Skipped => GetString("JobStatus.Skipped"),
        _ => status.ToString()
    };

    public string GetJobStatusBadge(LocalPrintJobStatus status) => status switch
    {
        LocalPrintJobStatus.Printed => GetString("JobStatusBadge.Printed"),
        LocalPrintJobStatus.Printing => GetString("JobStatusBadge.Printing"),
        LocalPrintJobStatus.Received => GetString("JobStatusBadge.Pending"),
        LocalPrintJobStatus.Failed => GetString("JobStatusBadge.Failed"),
        LocalPrintJobStatus.Skipped => GetString("JobStatusBadge.Skipped"),
        _ => GetJobStatus(status)
    };

    public string GetTrayIconState(TrayIconState state) => state switch
    {
        TrayIconState.Printing => GetString("Status.Printing"),
        TrayIconState.Polling => GetString("Status.Polling"),
        TrayIconState.Connected => GetString("Status.Connected"),
        TrayIconState.ConnectionLost => GetString("Status.ConnectionLost"),
        _ => GetString("Status.ConnectionLost")
    };

    public string GetTrayTooltip(TrayIconState state, string productName) => state switch
    {
        TrayIconState.Printing => GetString("Tray.Tooltip.Printing", productName),
        TrayIconState.Polling => GetString("Tray.Tooltip.Polling", productName),
        TrayIconState.Connected => GetString("Tray.Tooltip.Connected", productName),
        TrayIconState.ConnectionLost => GetString("Tray.Tooltip.ConnectionLost", productName),
        _ => productName
    };

    public string GetPlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return GetString("Common.Dash");

        var key = $"Platform.{platform}";
        var localized = ResourceManager.GetString(key, _cultureService.CurrentCulture)
            ?? ResourceManager.GetString(key, CultureInfo.GetCultureInfo(SupportedCultures.Default));
        return localized ?? platform;
    }

    public string GetServerConnectionStatus(BridgeServerConnectionStatus status) => status switch
    {
        BridgeServerConnectionStatus.Connected => GetString("ConnectionStatus.Connected"),
        BridgeServerConnectionStatus.Disconnected => GetString("ConnectionStatus.Disconnected"),
        BridgeServerConnectionStatus.Error => GetString("ConnectionStatus.Error"),
        BridgeServerConnectionStatus.Stopped => GetString("ConnectionStatus.Stopped"),
        _ => GetString("ConnectionStatus.Stopped")
    };

    public string GetPrinterHealthStatus(PrinterHealthStatus status) => status switch
    {
        PrinterHealthStatus.Ready => GetString("PrinterStatus.Ready"),
        PrinterHealthStatus.NotConfigured => GetString("PrinterStatus.NotConfigured"),
        PrinterHealthStatus.NotFound => GetString("PrinterStatus.NotFound"),
        PrinterHealthStatus.DryRun => GetString("PrinterStatus.DryRun"),
        _ => GetString("PrinterStatus.NotConfigured")
    };

    public string GetPrintHistoryFilter(PrintHistoryDateFilter filter) => filter switch
    {
        PrintHistoryDateFilter.Today => GetString("PrintHistory.Filter.Today"),
        PrintHistoryDateFilter.Last7Days => GetString("PrintHistory.Filter.Last7Days"),
        PrintHistoryDateFilter.Last30Days => GetString("PrintHistory.Filter.Last30Days"),
        _ => GetString("PrintHistory.Filter.Today")
    };

    public string GetReprintMessage(string messageKey) => messageKey switch
    {
        "PrintBridge.ReprintCreated" or "Reprint.Created" => GetString("Reprint.Created"),
        "PrintBridge.ReprintFailed" or "Reprint.Failed" => GetString("Reprint.Failed"),
        "PrintBridge.ReprintAlreadyPending" or "Reprint.AlreadyPending" => GetString("Reprint.AlreadyPending"),
        "PrintBridge.ReprintOrderNotFound" or "Reprint.OrderNotFound" => GetString("Reprint.OrderNotFound"),
        "PrintBridge.ReprintJobNotFound" or "Reprint.JobNotFound" => GetString("Reprint.JobNotFound"),
        "PrintBridge.ReprintNotAllowed" or "Reprint.NotAllowed" => GetString("Reprint.NotAllowed"),
        _ => GetString("Reprint.Failed")
    };

    public string GetTrayConnectionLabel(PrintBridgeRuntimeStatus status)
    {
        var state = GetServerConnectionStatus(status.ServerConnectionStatus);
        return GetString("Tray.ConnectionStatus", state);
    }

    public string GetHeaderBadge(PrintBridgeRuntimeStatus status)
    {
        if (status.IsRunning && status.IsConnected)
            return status.DryRun
                ? GetString("Status.RunningDryRun")
                : GetString("Status.Running");

        if (status.IsRunning)
            return GetString("Status.Running");

        if (status.IsConnected)
            return GetString("Status.Connected");

        return GetString("Status.Stopped");
    }

    public static string GetStringForCulture(string key, string cultureName, params object[] args)
    {
        var culture = SupportedCultures.GetCultureInfo(cultureName);
        var format = ResourceManager.GetString(key, culture)
            ?? ResourceManager.GetString(key, CultureInfo.GetCultureInfo(SupportedCultures.Default))
            ?? key;

        return args.Length > 0 ? string.Format(culture, format, args) : format;
    }
}
