using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeConnectionReset
{
    public static bool ApplyToSettings(WaslaOptions hub, PrintBridgeOptions bridge)
    {
        return PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(hub, bridge);
    }

    public static PrintBridgeRuntimeIssue CreateReconnectRequiredIssue() =>
        new(
            PrintBridgeRuntimeIssueCode.ReconnectRequired,
            ResourceKey: "RuntimeIssue.ManualReset.Detail");
}
