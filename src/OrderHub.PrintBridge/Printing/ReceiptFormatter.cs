using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderHub.PrintBridge.Printing;

public sealed class ReceiptFormatter
{
    private const int LineWidth = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string Format(string payloadJson, bool normalizeTurkishChars = true)
    {
        ReceiptPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReceiptPayload>(payloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
            return Normalize("OrderHub Receipt\nInvalid payload.", normalizeTurkishChars);

        var template = payload.Template;
        var useTemplate = template is not null;

        var sb = new StringBuilder();

        var header = ResolveHeader(payload, template, useTemplate);
        AppendCentered(sb, header);

        if (!string.IsNullOrWhiteSpace(header))
            AppendSeparator(sb);

        if (ShouldShow(template, useTemplate, t => t.ShowPlatformName) && !string.IsNullOrWhiteSpace(payload.Platform))
            AppendLine(sb, "Platform", payload.Platform);

        if (!string.IsNullOrWhiteSpace(payload.ExternalOrderCode))
            AppendLine(sb, "Order", payload.ExternalOrderCode);
        else if (!string.IsNullOrWhiteSpace(payload.ExternalOrderId))
            AppendLine(sb, "Order", payload.ExternalOrderId);

        if (ShouldShow(template, useTemplate, t => t.ShowReceivedTime) && payload.ReceivedAtUtc != default)
            AppendLine(sb, "Received", payload.ReceivedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

        var hasCustomerBlock = (ShouldShow(template, useTemplate, t => t.ShowCustomerName) && !string.IsNullOrWhiteSpace(payload.CustomerName))
            || (ShouldShow(template, useTemplate, t => t.ShowCustomerPhone) && !string.IsNullOrWhiteSpace(payload.CustomerPhone))
            || (ShouldShow(template, useTemplate, t => t.ShowDeliveryAddress) && !string.IsNullOrWhiteSpace(payload.DeliveryAddress));

        if (hasCustomerBlock)
            AppendSeparator(sb);

        if (ShouldShow(template, useTemplate, t => t.ShowCustomerName) && !string.IsNullOrWhiteSpace(payload.CustomerName))
            AppendLine(sb, "Customer", payload.CustomerName);
        if (ShouldShow(template, useTemplate, t => t.ShowCustomerPhone) && !string.IsNullOrWhiteSpace(payload.CustomerPhone))
            AppendLine(sb, "Phone", payload.CustomerPhone);
        if (ShouldShow(template, useTemplate, t => t.ShowDeliveryAddress) && !string.IsNullOrWhiteSpace(payload.DeliveryAddress))
            AppendWrapped(sb, "Address", payload.DeliveryAddress);

        if (payload.Items is { Count: > 0 })
        {
            AppendSeparator(sb);
            foreach (var item in payload.Items)
            {
                var qty = item.Quantity > 0 ? item.Quantity : 1;
                AppendWrapped(sb, null, $"{qty}x {item.ProductName}");

                if (item.LineTotal > 0)
                    AppendLine(sb, "  Line", item.LineTotal.ToString("0.00", CultureInfo.InvariantCulture));

                if (ShouldShow(template, useTemplate, t => t.ShowProductNotes) && !string.IsNullOrWhiteSpace(item.Notes))
                    AppendWrapped(sb, "  Note", item.Notes);

                if (ShouldShow(template, useTemplate, t => t.ShowProductOptions) && item.Options is { Count: > 0 })
                {
                    foreach (var opt in item.Options)
                    {
                        var price = opt.Price > 0 ? $" ({opt.Price:0.00})" : string.Empty;
                        AppendWrapped(sb, "  +", $"{opt.Name}{price}");
                    }
                }
            }
        }

        AppendSeparator(sb);

        if (ShouldShow(template, useTemplate, t => t.ShowSubtotal) && payload.Subtotal is > 0)
            AppendLine(sb, "Subtotal", payload.Subtotal.Value.ToString("0.00", CultureInfo.InvariantCulture));

        if (ShouldShow(template, useTemplate, t => t.ShowDeliveryFee) && payload.DeliveryFee is > 0)
            AppendLine(sb, "Delivery", payload.DeliveryFee.Value.ToString("0.00", CultureInfo.InvariantCulture));

        if (payload.TotalAmount > 0)
            AppendLine(sb, "TOTAL", payload.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture));

        if (ShouldShow(template, useTemplate, t => t.ShowPaymentMethod) && !string.IsNullOrWhiteSpace(payload.PaymentMethod))
            AppendLine(sb, "Payment", payload.PaymentMethod);

        if (ShouldShow(template, useTemplate, t => t.ShowFooterMessage))
        {
            var footer = ResolveFooter(payload, template, useTemplate);
            if (!string.IsNullOrWhiteSpace(footer))
            {
                AppendSeparator(sb);
                foreach (var line in footer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AppendCentered(sb, line);
            }
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();

        return Normalize(sb.ToString(), normalizeTurkishChars);
    }

    private static string? ResolveHeader(ReceiptPayload payload, ReceiptTemplatePayload? template, bool useTemplate)
    {
        if (useTemplate && template is not null)
        {
            if (!string.IsNullOrWhiteSpace(template.HeaderText))
                return template.HeaderText.Trim();
            if (template.ShowRestaurantName && !string.IsNullOrWhiteSpace(payload.TenantDisplayName))
                return payload.TenantDisplayName.Trim();
            return null;
        }

        return string.IsNullOrWhiteSpace(payload.TenantDisplayName) ? null : payload.TenantDisplayName.Trim();
    }

    private static string? ResolveFooter(ReceiptPayload payload, ReceiptTemplatePayload? template, bool useTemplate)
    {
        if (useTemplate && template is not null && !string.IsNullOrWhiteSpace(template.FooterText))
            return template.FooterText.Trim();
        return null;
    }

    private static bool ShouldShow(
        ReceiptTemplatePayload? template,
        bool useTemplate,
        Func<ReceiptTemplatePayload, bool> selector) =>
        !useTemplate || template is null || selector(template);

    private static void AppendCentered(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var line = text.Trim();
        if (line.Length >= LineWidth)
        {
            sb.AppendLine(line);
            return;
        }

        var pad = (LineWidth - line.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, pad)) + line);
    }

    private static void AppendSeparator(StringBuilder sb) => sb.AppendLine(new string('-', LineWidth));

    private static void AppendLine(StringBuilder sb, string? label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (string.IsNullOrWhiteSpace(label))
        {
            sb.AppendLine(Truncate(value));
            return;
        }

        var prefix = label + ": ";
        var room = LineWidth - prefix.Length;
        if (room < 8)
        {
            sb.AppendLine(prefix);
            AppendWrapped(sb, null, value);
            return;
        }

        sb.AppendLine(prefix + Truncate(value, room));
    }

    private static void AppendWrapped(StringBuilder sb, string? label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var words = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = string.IsNullOrWhiteSpace(label) ? string.Empty : label + " ";

        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current = word;
                continue;
            }

            if (current.Length + 1 + word.Length <= LineWidth)
                current += " " + word;
            else
            {
                sb.AppendLine(current);
                current = (label is null ? string.Empty : "  ") + word;
                label = null;
            }
        }

        if (current.Length > 0)
            sb.AppendLine(current);
    }

    private static string Truncate(string value, int max = LineWidth) =>
        value.Length <= max ? value : value[..max];

    private static string Normalize(string text, bool normalizeTurkishChars)
    {
        if (!normalizeTurkishChars) return text;
        return text
            .Replace('ç', 'c').Replace('Ç', 'C')
            .Replace('ğ', 'g').Replace('Ğ', 'G')
            .Replace('ı', 'i').Replace('İ', 'I')
            .Replace('ö', 'o').Replace('Ö', 'O')
            .Replace('ş', 's').Replace('Ş', 'S')
            .Replace('ü', 'u').Replace('Ü', 'U');
    }

    private sealed class ReceiptPayload
    {
        public string? TenantDisplayName { get; set; }
        public string? Platform { get; set; }
        public string? ExternalOrderId { get; set; }
        public string? ExternalOrderCode { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? DeliveryAddress { get; set; }
        public decimal? Subtotal { get; set; }
        public decimal? DeliveryFee { get; set; }
        public decimal TotalAmount { get; set; }
        public string? PaymentMethod { get; set; }
        public ReceiptTemplatePayload? Template { get; set; }
        public List<ReceiptItem>? Items { get; set; }
    }

    private sealed class ReceiptTemplatePayload
    {
        public string? HeaderText { get; set; }
        public string? FooterText { get; set; }
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
    }

    private sealed class ReceiptItem
    {
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineTotal { get; set; }
        public string? Notes { get; set; }
        public List<ReceiptItemOption>? Options { get; set; }
    }

    private sealed class ReceiptItemOption
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }
}
