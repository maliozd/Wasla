namespace Wasla.Application.Abstractions.Orders;

/// <summary>
/// Receipt creation timing options for tenant settings UI and mapping.
/// Persisted via <see cref="TenantOrderSettingsResult.AutoPrintReceiptOnAutoApprove"/> until a dedicated column exists.
/// </summary>
public static class ReceiptCreationTimingCodes
{
    public const string OnAccepted = "OnAccepted";
    public const string OnPreparing = "OnPreparing";
    public const string Manual = "Manual";

    public static string FromAutoPrintReceiptSetting(bool autoPrintReceiptOnAutoApprove) =>
        autoPrintReceiptOnAutoApprove ? OnAccepted : Manual;

    public static bool ToAutoPrintReceiptSetting(string? timing) =>
        !string.Equals(Normalize(timing), Manual, StringComparison.Ordinal);

    public static string Normalize(string? timing)
    {
        if (string.IsNullOrWhiteSpace(timing))
            return OnAccepted;

        var value = timing.Trim();
        if (string.Equals(value, OnAccepted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "onAccepted", StringComparison.Ordinal))
            return OnAccepted;

        if (string.Equals(value, OnPreparing, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "onPreparing", StringComparison.Ordinal))
            return OnPreparing;

        if (string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "manual", StringComparison.Ordinal))
            return Manual;

        return OnAccepted;
    }

    public static bool IsUserSelectable(string timing) =>
        timing is OnAccepted or Manual;
}
