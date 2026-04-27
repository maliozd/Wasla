using System.Collections.Concurrent;
using System.Text.Json;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;

namespace OrderHub.Infrastructure.Platform.Mock;

internal static class MockOrders
{
    private static readonly ConcurrentDictionary<FoodPlatform, ConcurrentQueue<string>> _knownExternalOrderIds = new();

    public static IReadOnlyCollection<ExternalOrderDto> CreateOrders(
        FoodPlatform platform,
        PlatformConnection connection,
        int count)
    {
        if (count <= 0) return Array.Empty<ExternalOrderDto>();

        var list = new List<ExternalOrderDto>(count);
        for (var i = 0; i < count; i++)
        {
            // Mock behavior for MVP:
            // - New orders use provider-like initial status strings (see GetInitialExternalStatus) that map to OrderStatus.New.
            // - Reused order ids (upsert) may only jump to final outcomes (Delivered/Cancelled), not random intermediates
            //   (intermediate echo strings come from IFoodPlatformClient + MockProviderOrderStatusStore after manual actions).
            var (externalOrderId, isExisting) = GetExternalOrderId(platform);
            var items = CreateItems(externalOrderId);

            var subtotal = items.Sum(x => x.TotalPrice);
            var deliveryFee = Random.Shared.Next(0, 2) == 0 ? 0m : 24.90m;
            var serviceFee = 5.00m;
            var total = subtotal + deliveryFee + serviceFee;

            var externalStatus = isExisting
                ? GetMockUpdateExternalStatus(platform)
                : GetInitialExternalStatus(platform);

            var dto = new ExternalOrderDto(
                Platform: platform,
                ExternalOrderId: externalOrderId,
                ExternalOrderCode: CreateOrderCode(platform),
                OrderedAtUtc: DateTime.UtcNow.AddMinutes(-Random.Shared.Next(1, 90)),
                CustomerName: $"Customer {Random.Shared.Next(1000, 9999)}",
                CustomerPhone: CreatePhone(),
                CustomerAddress: CreateAddress(),
                Subtotal: subtotal,
                DeliveryFee: deliveryFee,
                ServiceFee: serviceFee,
                Total: total,
                PaymentMethod: GetPaymentMethod(),
                PaymentStatus: GetPaymentStatus(),
                ExternalStatus: externalStatus,
                RawPayloadJson: CreateRawPayload(platform, connection, externalOrderId, externalStatus, items, subtotal, deliveryFee, serviceFee, total),
                Items: items);

            list.Add(dto);
        }

        return list;
    }

    private static IReadOnlyCollection<ExternalOrderItemDto> CreateItems(string externalOrderId)
    {
        var itemCount = Random.Shared.Next(1, 4);
        var items = new List<ExternalOrderItemDto>(itemCount);

        for (var i = 0; i < itemCount; i++)
        {
            var qty = Random.Shared.Next(1, 3);
            var unit = Random.Shared.Next(90, 350) + 0.90m;
            var total = unit * qty;

            var options = CreateOptions();
            var optionsTotal = options.Sum(o => o.Price);
            total += optionsTotal * qty;

            items.Add(new ExternalOrderItemDto(
                ExternalItemId: $"{externalOrderId}-item-{i + 1}",
                ProductName: GetProductName(),
                Quantity: qty,
                UnitPrice: unit,
                TotalPrice: total,
                Notes: Random.Shared.Next(0, 4) == 0 ? "No onions" : null,
                Options: options));
        }

        return items;
    }

    private static IReadOnlyCollection<ExternalOrderItemOptionDto> CreateOptions()
    {
        if (Random.Shared.Next(0, 3) == 0) return Array.Empty<ExternalOrderItemOptionDto>();

        var optionCount = Random.Shared.Next(1, 3);
        var options = new List<ExternalOrderItemOptionDto>(optionCount);

        for (var i = 0; i < optionCount; i++)
        {
            options.Add(new ExternalOrderItemOptionDto(
                Name: i == 0 ? "Extra cheese" : "Sauce",
                Price: i == 0 ? 14.90m : 9.90m));
        }

        return options;
    }

    private static (string ExternalOrderId, bool IsExisting) GetExternalOrderId(FoodPlatform platform)
    {
        var q = _knownExternalOrderIds.GetOrAdd(platform, _ => new ConcurrentQueue<string>());

        // ~30% reuse an existing ExternalOrderId to exercise idempotent upsert + updates
        if (Random.Shared.NextDouble() < 0.30 && q.TryPeek(out var existing))
        {
            return (existing, true);
        }

        var fresh = $"{platform}-{Guid.NewGuid():N}";
        q.Enqueue(fresh);

        // keep memory bounded
        while (q.Count > 200 && q.TryDequeue(out _)) { }

        return (fresh, false);
    }

    private static string GetInitialExternalStatus(FoodPlatform platform) =>
        platform switch
        {
            // Partner / provider-shaped strings mapped to New (see DefaultOrderStatusMapper).
            FoodPlatform.Yemeksepeti => "RECEIVED",
            FoodPlatform.GetirYemek => "CREATED",
            FoodPlatform.TrendyolYemek => "Created",
            _ => "unknown"
        };

    private static string GetMockUpdateExternalStatus(FoodPlatform platform)
    {
        // Simple, predictable demo behavior:
        // 70% stays New, 20% becomes Delivered, 10% becomes Cancelled.
        var roll = Random.Shared.NextDouble();
        if (roll < 0.70) return GetInitialExternalStatus(platform);

        var toDelivered = roll < 0.90;

        return platform switch
        {
            FoodPlatform.Yemeksepeti => toDelivered ? "DELIVERED" : "CANCELLED",
            FoodPlatform.GetirYemek => toDelivered ? "DELIVERED" : "CANCELLED",
            FoodPlatform.TrendyolYemek => toDelivered ? "Delivered" : "Cancelled",
            _ => GetInitialExternalStatus(platform)
        };
    }

    private static PaymentMethod GetPaymentMethod() =>
        Pick(PaymentMethod.Cash, PaymentMethod.CreditCard, PaymentMethod.OnlinePayment);

    private static PaymentStatus GetPaymentStatus() =>
        Pick(PaymentStatus.Pending, PaymentStatus.Paid, PaymentStatus.Failed);

    private static string GetProductName() =>
        Pick("Chicken Döner", "Cheeseburger", "Pizza Margherita", "Köfte Sandwich", "Salad Bowl");

    private static string CreateRawPayload(
        FoodPlatform platform,
        PlatformConnection connection,
        string externalOrderId,
        string externalStatus,
        IReadOnlyCollection<ExternalOrderItemDto> items,
        decimal subtotal,
        decimal deliveryFee,
        decimal serviceFee,
        decimal total)
    {
        var payload = new
        {
            platform = platform.ToString(),
            storeId = connection.StoreId,
            externalOrderId,
            externalStatus,
            orderedAtUtc = DateTime.UtcNow,
            totals = new { subtotal, deliveryFee, serviceFee, total },
            items = items.Select(i => new
            {
                i.ExternalItemId,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                options = i.Options.Select(o => new { o.Name, o.Price })
            })
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string CreateOrderCode(FoodPlatform platform)
    {
        var prefix = platform switch
        {
            FoodPlatform.Yemeksepeti => "YS",
            FoodPlatform.GetirYemek => "GY",
            FoodPlatform.TrendyolYemek => "TY",
            _ => "OH"
        };
        return $"{prefix}-ORDER-{Random.Shared.Next(10000, 99999)}";
    }

    private static string CreatePhone() => $"+9055{Random.Shared.Next(10000000, 99999999)}";

    private static string CreateAddress() =>
        Pick("Kadıköy, İstanbul", "Beşiktaş, İstanbul", "Üsküdar, İstanbul", "Şişli, İstanbul", "Ataşehir, İstanbul");

    private static T Pick<T>(params T[] values) => values[Random.Shared.Next(values.Length)];
}

