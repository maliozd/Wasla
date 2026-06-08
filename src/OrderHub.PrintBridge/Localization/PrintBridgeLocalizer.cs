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

    public string GetJobStatus(LocalPrintJobStatus status) => status switch
    {
        LocalPrintJobStatus.Received => GetString("JobStatus.Pending"),
        LocalPrintJobStatus.Printing => GetString("JobStatus.Printing"),
        LocalPrintJobStatus.Printed => GetString("JobStatus.Printed"),
        LocalPrintJobStatus.Failed => GetString("JobStatus.Failed"),
        LocalPrintJobStatus.Skipped => GetString("JobStatus.Skipped"),
        _ => status.ToString()
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
