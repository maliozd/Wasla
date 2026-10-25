using Wasla.Application.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-5: a deterministic, fake copy of the active-order set that kept a developer's Live Screen on "Loading" on
/// 14 October 2026 (5,754 active orders, 5,316 New and 438 Accepted, oldest eight days old, 53 from the current day,
/// about 5 MB of snapshot). Added on top of <see cref="TenantVisualWebHost"/>'s own five active orders, it makes the
/// Live Screen snapshot hold <see cref="ActiveOrders"/> active orders. Fixed seed, fake names, no provider or printer.
/// The host's recently delivered order is dated before the Delivered window, so the snapshot holds no order that leaves
/// the Live Screen by itself while the tests run.
/// </summary>
internal static class LargeActiveOrderSeed
{
    public const int NewOrders = 5312;
    public const int AcceptedOrders = 438;
    public const int TodayOrders = 53;

    /// <summary>The seeded orders plus the host's own active ones (New, Accepted, Preparing, Ready, On the way).</summary>
    public const int ActiveOrders = NewOrders + AcceptedOrders + 5;

    private static readonly string[] Products =
    [
        "Adana Kebap", "Lahmacun", "Ayran", "Künefe", "Mercimek Çorbası", "Tavuk Şiş", "İskender", "Pide (Kaşarlı)",
        "Çoban Salata", "Baklava (4 dilim)", "Su", "Kola", "Patates Kızartması", "Köfte Ekmek", "Mantı", "Sütlaç"
    ];

    private static readonly string[] Customers =
    [
        "Zeynep Kaya", "Can Demir", "Elif Şahin", "Burak Yılmaz", "Selin Arslan", "Deniz Aydın", "Okan Çelik", "Ece Koç"
    ];

    private static readonly string[] ItemNotes = ["Extra spicy", "No onions", "Well done", "Sauce on the side"];

    private static readonly string[] OrderNotes =
    [
        "Please ring the bell twice.", "No cutlery.", "Leave at the door, baby sleeping.", "Call on arrival."
    ];

    public static void Seed(TenantDbContext db, DateTime now)
    {
        var random = new Random(5755);
        var platforms = new[] { FoodPlatform.Yemeksepeti, FoodPlatform.TrendyolYemek, FoodPlatform.GetirYemek };
        var total = NewOrders + AcceptedOrders;
        var orders = new List<Order>(total);
        var accepted = 0;
        for (var i = 0; i < total; i++)
        {
            // 53 orders from the last two hours, the rest spread over the previous eight days.
            var receivedAt = i < TodayOrders
                ? now.AddMinutes(-(i * 2 + 3))
                : now.AddDays(-1).AddMinutes(-(double)(i - TodayOrders) * (7 * 24 * 60) / (total - TodayOrders));
            var status = i % 13 == 5 && accepted < AcceptedOrders ? OrderStatus.Accepted : OrderStatus.New;
            if (status == OrderStatus.Accepted)
                accepted++;
            var platform = platforms[i % platforms.Length];
            var code = $"{(platform == FoodPlatform.Yemeksepeti ? "YS" : platform == FoodPlatform.TrendyolYemek ? "TY" : "GY")}-{900000 + i}";
            var itemCount = 1 + random.Next(5);
            var items = new List<OrderItem>(itemCount);
            decimal sum = 0;
            for (var n = 0; n < itemCount; n++)
            {
                var quantity = 1 + random.Next(3);
                var unit = 25m + random.Next(40) * 5m;
                sum += unit * quantity;
                items.Add(new OrderItem
                {
                    ProductName = Products[random.Next(Products.Length)],
                    Quantity = quantity,
                    UnitPrice = unit,
                    TotalPrice = unit * quantity,
                    Notes = random.Next(100) < 25 ? ItemNotes[random.Next(ItemNotes.Length)] : null,
                    Sequence = n
                });
            }

            orders.Add(new Order
            {
                Id = new Guid($"55000000-0000-4000-8000-{i:D12}"),
                Platform = platform,
                ExternalOrderId = code,
                ExternalOrderCode = code,
                IdempotencyKey = $"{platform}:{code}",
                InternalStatus = status,
                PlatformStatus = status.ToString(),
                CustomerName = Customers[random.Next(Customers.Length)],
                CustomerPhone = "5551112233",
                CustomerAddress = $"Caferağa Mh. Moda Cd. No: {1 + random.Next(120)}, Kadıköy / İstanbul",
                CustomerNote = random.Next(100) < 35 ? OrderNotes[random.Next(OrderNotes.Length)] : null,
                TotalAmount = sum,
                DeliveryFee = 19.90m,
                PaymentMethod = PaymentMethod.OnlinePayment,
                PaymentStatus = PaymentStatus.Paid,
                CreatedAtPlatform = receivedAt,
                ReceivedAt = receivedAt,
                AcceptedAt = status == OrderStatus.Accepted ? receivedAt.AddMinutes(1) : null,
                RawPayloadJson = "{}",
                Items = items
            });
        }

        db.Orders.AddRange(orders);

        // The host's recently delivered order leaves the Live Screen when its Delivered window ends, about a minute after
        // seeding and so in the middle of the class: a refresh the tests treat as unchanged then removed its card
        // (GitHub #70). Delivered earlier, it is not in the snapshot, which then changes only when a test changes an order.
        var windowStart = now - LiveScreenVisibility.RecentDeliveredWindow;
        foreach (var delivered in db.Orders.Where(o => o.InternalStatus == OrderStatus.Delivered).ToList())
        {
            if (delivered.DeliveredAt > windowStart)
                delivered.DeliveredAt = windowStart.AddMinutes(-10);
        }
    }
}
