using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgeConnectionReset
{
    public static bool ApplyToSettings(WaslaOptions connection, PrintBridgeOptions bridge)
    {
        return PrintBridgeRuntimeCredentialFallback.ClearTokenForReconnectRequired(connection, bridge);
    }

    public static PrintBridgeRuntimeIssue CreateReconnectRequiredIssue() =>
        new(
            PrintBridgeRuntimeIssueCode.ReconnectRequired,
            ResourceKey: "RuntimeIssue.ManualReset.Detail");
}
