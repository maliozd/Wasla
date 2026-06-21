using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeSettingsValidator
{
    public const int MinIdlePollIntervalSeconds = 1;
    public const int MaxIdlePollIntervalSeconds = 300;
    public const int MinBusyPollIntervalSeconds = 1;
    public const int MaxBusyPollIntervalSeconds = 60;
    public const int MinErrorPollIntervalSeconds = 1;
    public const int MaxErrorPollIntervalSeconds = 300;

    public static bool TryValidate(WaslaOptions orderHub, PrintBridgeOptions bridge, out string? errorKey)
    {
        if (!TryNormalize(orderHub, bridge, out errorKey))
            return false;

        errorKey = null;
        return true;
    }

    public static bool TryNormalize(WaslaOptions orderHub, PrintBridgeOptions bridge, out string? errorKey)
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

        if (bridge.IdlePollIntervalSeconds is < MinIdlePollIntervalSeconds or > MaxIdlePollIntervalSeconds
            || bridge.BusyPollIntervalSeconds is < MinBusyPollIntervalSeconds or > MaxBusyPollIntervalSeconds
            || bridge.ErrorPollIntervalSeconds is < MinErrorPollIntervalSeconds or > MaxErrorPollIntervalSeconds)
        {
            errorKey = "Validation.InvalidServerUrl";
            return false;
        }

        if (!bridge.DryRun && string.IsNullOrWhiteSpace(bridge.PrinterName))
        {
            errorKey = "Validation.PrinterRequired";
            return false;
        }

        orderHub.ServerUrl = NormalizeServerUrl(uri);
        orderHub.AgentToken = orderHub.AgentToken.Trim();
        bridge.PrinterName = bridge.PrinterName?.Trim() ?? string.Empty;
        bridge.MachineName = string.IsNullOrWhiteSpace(bridge.MachineName)
            ? Environment.MachineName
            : bridge.MachineName.Trim();
        bridge.DisplayName = bridge.DisplayName?.Trim() ?? string.Empty;
        bridge.BridgeName = bridge.BridgeName?.Trim() ?? string.Empty;

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
}
