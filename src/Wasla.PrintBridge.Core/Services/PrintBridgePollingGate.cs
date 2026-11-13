using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.Services;

public static class PrintBridgePollingGate
{
    public static bool RequiresReconnectBeforeStart(string? agentToken, PrintBridgeRuntimeIssue? lastIssue) =>
        string.IsNullOrWhiteSpace(agentToken?.Trim())
        || lastIssue is { Code: PrintBridgeRuntimeIssueCode.ReconnectRequired };

    /// <summary>The same rule for a client that only knows whether a token is saved, never the token (WAS-61).</summary>
    public static bool RequiresReconnectBeforeStart(bool hasSavedToken, PrintBridgeRuntimeIssue? lastIssue) =>
        !hasSavedToken || lastIssue is { Code: PrintBridgeRuntimeIssueCode.ReconnectRequired };
}
