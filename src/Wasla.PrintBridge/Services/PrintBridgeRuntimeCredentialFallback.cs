using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeRuntimeCredentialFallback
{
    public static bool ClearTokenForReconnectRequired(WaslaOptions hub, PrintBridgeOptions bridge)
    {
        var changed = !string.IsNullOrWhiteSpace(hub.AgentToken)
            || !string.IsNullOrWhiteSpace(bridge.DisplayName)
            || bridge.ServerDeviceNameResolved;

        hub.AgentToken = string.Empty;
        bridge.DisplayName = string.Empty;
        bridge.ServerDeviceNameResolved = false;

        return changed;
    }
}
