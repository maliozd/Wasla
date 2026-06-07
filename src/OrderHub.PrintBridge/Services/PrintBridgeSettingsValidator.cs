using OrderHub.PrintBridge.Options;

namespace OrderHub.PrintBridge.Services;

public static class PrintBridgeSettingsValidator
{
    public static bool TryValidate(OrderHubOptions orderHub, PrintBridgeOptions bridge, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(orderHub.BaseUrl))
        {
            errorMessage = "OrderHub BaseUrl is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(orderHub.AgentToken))
        {
            errorMessage = "Agent token is required.";
            return false;
        }

        if (!bridge.DryRun && string.IsNullOrWhiteSpace(bridge.PrinterName))
        {
            errorMessage = "Printer name is required when DryRun is disabled.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
