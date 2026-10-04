using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.UI;

/// <summary>
/// Detects when a user pastes a setup/protocol value into the device-token field.
/// Pure logic (no WinForms) so it can be unit-tested and kept layout-independent.
/// </summary>
internal static class PrintBridgeTokenPasteGuard
{
    public static bool LooksLikeSetupOrProtocolValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Contains(PrintBridgeProtocolUri.Scheme + "://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(PrintBridgeProtocolUri.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            return true;

        // Partial protocol query paste, e.g. server=https://...&code=...
        if (trimmed.Contains("server=", StringComparison.OrdinalIgnoreCase)
            && trimmed.Contains("code=", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Contains('&') || trimmed.Contains('?')))
            return true;

        if (PrintBridgeProtocolUri.TryParseSetup(trimmed, out _, out _))
            return true;

        return false;
    }
}
