using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgePollingGate
{
    public static bool RequiresReconnectBeforeStart(string? agentToken, PrintBridgeRuntimeIssue? lastIssue) =>
        string.IsNullOrWhiteSpace(agentToken?.Trim())
        || lastIssue is { Code: PrintBridgeRuntimeIssueCode.ReconnectRequired };
}
