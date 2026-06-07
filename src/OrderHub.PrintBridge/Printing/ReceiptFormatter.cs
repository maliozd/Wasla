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

        var sb = new StringBuilder();
        AppendCentered(sb, payload.TenantDisplayName);
        AppendSeparator(sb);

        if (!string.IsNullOrWhiteSpace(payload.Platform))
            AppendLine(sb, "Platform", payload.Platform);
        if (!string.IsNullOrWhiteSpace(payload.ExternalOrderCode))
            AppendLine(sb, "Order", payload.ExternalOrderCode);
        else if (!string.IsNullOrWhiteSpace(payload.ExternalOrderId))
            AppendLine(sb, "Order", payload.ExternalOrderId);

        if (payload.ReceivedAtUtc != default)
            AppendLine(sb, "Received", payload.ReceivedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

        AppendSeparator(sb);

        if (!string.IsNullOrWhiteSpace(payload.CustomerName))
            AppendLine(sb, "Customer", payload.CustomerName);
        if (!string.IsNullOrWhiteSpace(payload.CustomerPhone))
            AppendLine(sb, "Phone", payload.CustomerPhone);
        if (!string.IsNullOrWhiteSpace(payload.DeliveryAddress))
            AppendWrapped(sb, "Address", payload.DeliveryAddress);

        if (payload.Items is { Count: > 0 })
        {
            AppendSeparator(sb);
            foreach (var item in payload.Items)
            {
                var qty = item.Quantity > 0 ? item.Quantity : 1;
                AppendWrapped(sb, null, $"{qty}x {item.ProductName}");

                if (item.UnitPrice > 0)
                    AppendLine(sb, "  Unit", item.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture));
                if (item.LineTotal > 0)
                    AppendLine(sb, "  Line", item.LineTotal.ToString("0.00", CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(item.Notes))
                    AppendWrapped(sb, "  Note", item.Notes);

                if (item.Options is { Count: > 0 })
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

        if (payload.TotalAmount > 0)
            AppendLine(sb, "TOTAL", payload.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(payload.PaymentMethod))
            AppendLine(sb, "Payment", payload.PaymentMethod);

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine();

        return Normalize(sb.ToString(), normalizeTurkishChars);
    }

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
        public decimal TotalAmount { get; set; }
        public string? PaymentMethod { get; set; }
        public List<ReceiptItem>? Items { get; set; }
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
