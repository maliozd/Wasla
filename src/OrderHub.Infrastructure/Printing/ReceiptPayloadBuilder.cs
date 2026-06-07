using System.Text.Json;
using System.Text.Json.Serialization;
using OrderHub.Domain.Entities.Customer;

namespace OrderHub.Infrastructure.Printing;

internal static class ReceiptPayloadBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Build(Order order, string? tenantDisplayName)
    {
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
            TotalAmount = order.TotalAmount,
            PaymentMethod = order.PaymentMethod == Domain.Enums.PaymentMethod.Unknown
                ? null
                : order.PaymentMethod.ToString(),
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
        public decimal TotalAmount { get; init; }
        public string? PaymentMethod { get; init; }
        public List<ReceiptItemSnapshot> Items { get; init; } = [];
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
