using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeRuntimeCredentialFallback
{
    public static bool ClearTokenForReconnectRequired(WaslaOptions connection, PrintBridgeOptions bridge)
    {
        var changed = !string.IsNullOrWhiteSpace(connection.AgentToken)
            || !string.IsNullOrWhiteSpace(bridge.DisplayName)
            || bridge.ServerDeviceNameResolved;

        connection.AgentToken = string.Empty;
        bridge.DisplayName = string.Empty;
        bridge.ServerDeviceNameResolved = false;

        return changed;
    }
}
