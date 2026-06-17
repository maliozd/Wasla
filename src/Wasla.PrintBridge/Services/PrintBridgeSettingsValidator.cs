using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeSettingsValidator
{
    public static bool TryValidate(WaslaOptions orderHub, PrintBridgeOptions bridge, out string? errorKey)
    {
        if (string.IsNullOrWhiteSpace(orderHub.ServerUrl))
        {
            errorKey = "Validation.ServerUrlRequired";
            return false;
        }

        if (!Uri.TryCreate(orderHub.ServerUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errorKey = "Validation.InvalidServerUrl";
            return false;
        }

        if (string.IsNullOrWhiteSpace(orderHub.AgentToken))
        {
            errorKey = "Validation.AgentTokenRequired";
            return false;
        }

        if (!bridge.DryRun && string.IsNullOrWhiteSpace(bridge.PrinterName))
        {
            errorKey = "Validation.PrinterRequired";
            return false;
        }

        errorKey = null;
        return true;
    }
}
