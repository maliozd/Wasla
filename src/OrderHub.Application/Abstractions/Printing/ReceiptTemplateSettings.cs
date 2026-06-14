namespace OrderHub.Application.Abstractions.Printing;

using OrderHub.Application.Printing;

public static class ReceiptTemplateLimits
{
    public const int HeaderMaxLength = 60;
    public const int FooterMaxLength = 160;
}

/// <summary>
/// Per-tenant receipt layout/content preferences. Optional fields may be hidden on printed receipts.
/// Required receipt fields (order code, items, totals) are always rendered.
/// </summary>
public sealed class ReceiptTemplateSettings
{
    public bool ShowRestaurantName { get; set; } = true;
    public bool ShowPlatformName { get; set; } = true;
    public bool ShowReceivedTime { get; set; } = true;
    public bool ShowCustomerName { get; set; } = true;
    public bool ShowCustomerPhone { get; set; } = true;
    public bool ShowDeliveryAddress { get; set; } = true;
    public bool ShowProductNotes { get; set; } = true;
    public bool ShowProductOptions { get; set; } = true;
    public bool ShowSubtotal { get; set; } = true;
    public bool ShowDiscount { get; set; }
    public bool ShowDeliveryFee { get; set; } = true;
    public bool ShowPaymentMethod { get; set; }
    public bool ShowFooterMessage { get; set; } = true;
    public string ReceiptLanguage { get; set; } = ReceiptLanguageCodes.Turkish;
    public string? ReceiptHeaderText { get; set; }
    public string? ReceiptFooterText { get; set; }

    public static ReceiptTemplateSettings CreateDefaults(string? customerDisplayName, string? defaultReceiptLanguage = null)
    {
        var language = ReceiptLanguageCodes.Normalize(defaultReceiptLanguage);
        return new ReceiptTemplateSettings
        {
            ShowRestaurantName = true,
            ReceiptLanguage = language,
            ReceiptHeaderText = null,
            ReceiptFooterText = ReceiptLabelLocalizer.GetDefaultFooter(language)
        };
    }

    public static string? NormalizeSingleLine(string? value, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > maxLength)
            normalized = normalized[..maxLength];
        return normalized;
    }

    public static string? NormalizeFooter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var joined = string.Join("\n", lines.Select(l => NormalizeSingleLine(l, ReceiptTemplateLimits.FooterMaxLength) ?? string.Empty).Where(l => l.Length > 0));
        if (joined.Length > ReceiptTemplateLimits.FooterMaxLength)
            joined = joined[..ReceiptTemplateLimits.FooterMaxLength];
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }
}

public sealed record UpdateReceiptTemplateSettingsCommand(
    bool ShowRestaurantName,
    bool ShowPlatformName,
    bool ShowReceivedTime,
    bool ShowCustomerName,
    bool ShowCustomerPhone,
    bool ShowDeliveryAddress,
    bool ShowProductNotes,
    bool ShowProductOptions,
    bool ShowSubtotal,
    bool ShowDiscount,
    bool ShowDeliveryFee,
    bool ShowPaymentMethod,
    bool ShowFooterMessage,
    string ReceiptLanguage,
    string? ReceiptHeaderText,
    string? ReceiptFooterText);

public interface IReceiptTemplateSettingsService
{
    Task<ReceiptTemplateSettings> GetAsync(Guid customerId, string? customerDisplayName, string? defaultReceiptLanguage, CancellationToken ct);
    Task<ReceiptTemplateSettings> UpdateAsync(Guid customerId, string? customerDisplayName, UpdateReceiptTemplateSettingsCommand command, CancellationToken ct);
}
