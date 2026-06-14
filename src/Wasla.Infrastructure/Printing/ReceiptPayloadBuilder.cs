using System.Text.Json;
using System.Text.Json.Serialization;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Customer;

namespace Wasla.Infrastructure.Printing;

internal static class ReceiptPayloadBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Build(
        Order order,
        string? tenantDisplayName,
        ReceiptTemplateSettings? template = null)
    {
        var subtotal = order.Items.Sum(i => i.TotalPrice);

        var payload = new ReceiptPayloadSnapshot
        {
            TenantDisplayName = string.IsNullOrWhiteSpace(tenantDisplayName) ? null : tenantDisplayName.Trim(),
            Platform = order.Platform.ToString(),
            ExternalOrderId = NullIfEmpty(order.ExternalOrderId),
            ExternalOrderCode = NullIfEmpty(order.ExternalOrderCode),
            ReceivedAtUtc = order.ReceivedAt,
            CustomerName = NullIfEmpty(order.CustomerName),
            CustomerPhone = NullIfEmpty(order.CustomerPhone),
            DeliveryAddress = NullIfEmpty(order.CustomerAddress),
            Subtotal = subtotal > 0 ? subtotal : null,
            DeliveryFee = order.DeliveryFee > 0 ? order.DeliveryFee : null,
            TotalAmount = order.TotalAmount,
            PaymentMethod = order.PaymentMethod == Domain.Enums.PaymentMethod.Unknown
                ? null
                : order.PaymentMethod.ToString(),
            Template = template is null ? null : MapTemplate(template),
            Items = order.Items.Select(item => new ReceiptItemSnapshot
            {
                ProductName = item.ProductName,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.TotalPrice,
                Notes = NullIfEmpty(item.Notes),
                Options = item.Options.Count == 0
                    ? null
                    : item.Options.Select(o => new ReceiptItemOptionSnapshot
                    {
                        Name = o.Name,
                        Price = o.Price
                    }).ToList()
            }).ToList()
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static ReceiptTemplatePayload MapTemplate(ReceiptTemplateSettings template) =>
        new()
        {
            HeaderText = template.ReceiptHeaderText,
            FooterText = template.ReceiptFooterText,
            ShowRestaurantName = template.ShowRestaurantName,
            ShowPlatformName = template.ShowPlatformName,
            ShowReceivedTime = template.ShowReceivedTime,
            ShowCustomerName = template.ShowCustomerName,
            ShowCustomerPhone = template.ShowCustomerPhone,
            ShowDeliveryAddress = template.ShowDeliveryAddress,
            ShowProductNotes = template.ShowProductNotes,
            ShowProductOptions = template.ShowProductOptions,
            ShowSubtotal = template.ShowSubtotal,
            ShowDiscount = template.ShowDiscount,
            ShowDeliveryFee = template.ShowDeliveryFee,
            ShowPaymentMethod = template.ShowPaymentMethod,
            ShowFooterMessage = template.ShowFooterMessage,
            Language = template.ReceiptLanguage
        };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ReceiptPayloadSnapshot
    {
        public string? TenantDisplayName { get; init; }
        public string Platform { get; init; } = string.Empty;
        public string? ExternalOrderId { get; init; }
        public string? ExternalOrderCode { get; init; }
        public DateTime ReceivedAtUtc { get; init; }
        public string? CustomerName { get; init; }
        public string? CustomerPhone { get; init; }
        public string? DeliveryAddress { get; init; }
        public decimal? Subtotal { get; init; }
        public decimal? DeliveryFee { get; init; }
        public decimal TotalAmount { get; init; }
        public string? PaymentMethod { get; init; }
        public ReceiptTemplatePayload? Template { get; init; }
        public List<ReceiptItemSnapshot> Items { get; init; } = [];
    }

    private sealed class ReceiptTemplatePayload
    {
        public string? HeaderText { get; init; }
        public string? FooterText { get; init; }
        public bool ShowRestaurantName { get; init; } = true;
        public bool ShowPlatformName { get; init; } = true;
        public bool ShowReceivedTime { get; init; } = true;
        public bool ShowCustomerName { get; init; } = true;
        public bool ShowCustomerPhone { get; init; } = true;
        public bool ShowDeliveryAddress { get; init; } = true;
        public bool ShowProductNotes { get; init; } = true;
        public bool ShowProductOptions { get; init; } = true;
        public bool ShowSubtotal { get; init; } = true;
        public bool ShowDiscount { get; init; }
        public bool ShowDeliveryFee { get; init; } = true;
        public bool ShowPaymentMethod { get; init; }
        public bool ShowFooterMessage { get; init; } = true;
        public string Language { get; init; } = ReceiptLanguageCodes.Turkish;
    }

    private sealed class ReceiptItemSnapshot
    {
        public string ProductName { get; init; } = string.Empty;
        public int Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal LineTotal { get; init; }
        public string? Notes { get; init; }
        public List<ReceiptItemOptionSnapshot>? Options { get; init; }
    }

    private sealed class ReceiptItemOptionSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public decimal Price { get; init; }
    }
}
