using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Printing;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeSettingsValidator
{
    public const int MaxDeviceDisplayNameLength = 200;
    public const int MinIdlePollIntervalSeconds = 1;
    public const int MaxIdlePollIntervalSeconds = 300;
    public const int MinBusyPollIntervalSeconds = 1;
    public const int MaxBusyPollIntervalSeconds = 60;
    public const int MinErrorPollIntervalSeconds = 1;
    public const int MaxErrorPollIntervalSeconds = 300;

    public static bool TryValidate(WaslaOptions orderHub, PrintBridgeOptions bridge, out string? errorKey)
    {
        if (!TryValidatePrintingReadiness(orderHub, bridge, out errorKey))
            return false;

        errorKey = null;
        return true;
    }

    public static bool TryNormalize(WaslaOptions orderHub, PrintBridgeOptions bridge, out string? errorKey)
        => TryValidatePrintingReadiness(orderHub, bridge, out errorKey);

    public static bool TryValidateConnectionSettings(WaslaOptions orderHub, out string? errorKey)
    {
        var serverUrl = orderHub.ServerUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            errorKey = "Validation.ServerUrlRequired";
            return false;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errorKey = "Validation.InvalidServerUrl";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errorKey = "Validation.InvalidServerUrl";
            return false;
        }

        if (string.IsNullOrWhiteSpace(orderHub.AgentToken))
        {
            errorKey = "Validation.AgentTokenRequired";
            return false;
        }

        orderHub.ServerUrl = NormalizeServerUrl(uri);
        orderHub.AgentToken = orderHub.AgentToken.Trim();

        errorKey = null;
        return true;
    }

    public static bool TryValidatePrinterSettings(PrintBridgeOptions bridge, out string? errorKey)
    {
        NormalizePrinterFields(bridge);

        if (!TryValidateAdvancedBehaviorSettings(bridge, out errorKey))
            return false;

        if (string.IsNullOrWhiteSpace(bridge.PrinterName))
        {
            errorKey = "Validation.PrinterRequired";
            return false;
        }

        errorKey = null;
        return true;
    }

    public static bool TryValidateAdvancedBehaviorSettings(PrintBridgeOptions bridge, out string? errorKey)
    {
        if (bridge.IdlePollIntervalSeconds is < MinIdlePollIntervalSeconds or > MaxIdlePollIntervalSeconds
            || bridge.BusyPollIntervalSeconds is < MinBusyPollIntervalSeconds or > MaxBusyPollIntervalSeconds
            || bridge.ErrorPollIntervalSeconds is < MinErrorPollIntervalSeconds or > MaxErrorPollIntervalSeconds)
        {
            errorKey = "Validation.InvalidPollingIntervals";
            return false;
        }

        errorKey = null;
        return true;
    }

    public static bool TryValidateDeviceIdentitySettings(PrintBridgeOptions bridge, out string? errorKey)
    {
        bridge.MachineName = string.IsNullOrWhiteSpace(bridge.MachineName)
            ? Environment.MachineName
            : bridge.MachineName.Trim();
        bridge.DisplayName = Truncate(bridge.DisplayName?.Trim() ?? string.Empty, MaxDeviceDisplayNameLength);
        bridge.BridgeName = bridge.BridgeName?.Trim() ?? string.Empty;
        bridge.BridgeName = Truncate(bridge.BridgeName, MaxDeviceDisplayNameLength);

        errorKey = null;
        return true;
    }

    public static bool TryValidatePrintingReadiness(
        WaslaOptions orderHub,
        PrintBridgeOptions bridge,
        out string? errorKey)
    {
        if (!TryValidateConnectionSettings(orderHub, out errorKey))
            return false;

        if (!TryValidatePrinterSettings(bridge, out errorKey))
            return false;

        if (!TryValidateDeviceIdentitySettings(bridge, out errorKey))
            return false;

        errorKey = null;
        return true;
    }

    public static bool TryValidatePrinterAvailability(PrintBridgeOptions bridge, out string? errorKey)
    {
        if (!TryValidatePrinterSettings(bridge, out errorKey))
            return false;

        if (!string.IsNullOrWhiteSpace(bridge.PrinterName) && !RawPrinterHelper.PrinterExists(bridge.PrinterName))
        {
            errorKey = "Error.PrinterUnavailable";
            return false;
        }

        errorKey = null;
        return true;
    }

    private static string NormalizeServerUrl(Uri uri)
    {
        var normalized = uri.GetComponents(
            UriComponents.SchemeAndServer
            | UriComponents.Path,
            UriFormat.UriEscaped);

        return normalized.TrimEnd('/');
    }

    private static void NormalizePrinterFields(PrintBridgeOptions bridge)
    {
        bridge.PrinterMode = string.IsNullOrWhiteSpace(bridge.PrinterMode)
            ? "WindowsPrinter"
            : bridge.PrinterMode.Trim();
        bridge.PrinterName = bridge.PrinterName?.Trim() ?? string.Empty;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
