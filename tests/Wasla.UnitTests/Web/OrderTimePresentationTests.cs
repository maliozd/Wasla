using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Application.Orders;
using Wasla.Application.Time;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Web;

/// <summary>
/// WAS-125 on the rendered pages, in every UI culture: Orders, Dashboard and order details label each time with its
/// meaning. A recovery import shows the provider's order time with the later import time under it; an order without a
/// provider time shows the import time under the "received in Wasla" label, never as the order time.
/// </summary>
public sealed partial class OrderDataPresentationTests
{
    [Fact]
    public async Task OrderTimes_AreLabelledByMeaning_OnOrdersDashboardAndDetails_InEveryCulture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync();

        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        // Today in Turkey and the fallback order's import time come from one instant, and the import time stays in that
        // day (a minute ago, or the day's first moment just after midnight), so it counts as today.
        var nowUtc = DateTime.UtcNow;
        var (today, fallbackReceivedUtc) = TurkeyTodayAt(nowUtc, nowUtc.AddMinutes(-1));
        var yesterday = today.AddDays(-1);
        // Placed at 20:40 yesterday in Turkey and imported now by a recovery poll, like the three production orders.
        var placedUtc = TimeZoneInfo.ConvertTimeToUtc(yesterday.ToDateTime(new TimeOnly(20, 40)), tz);

        var todayBefore = await TodayOrderCountAsync(host, ct);
        var recoveryId = await SeedAsync(host, "TY-RECOVERY-125", placedUtc, nowUtc, ct);
        Assert.Equal(todayBefore, await TodayOrderCountAsync(host, ct));
        var fallbackId = await SeedAsync(host, "TY-NOTIME-125", null, fallbackReceivedUtc, ct);
        Assert.Equal(todayBefore + 1, await TodayOrderCountAsync(host, ct));

        foreach (var name in Cultures)
        {
            var resources = Resources("." + name);
            var orderTime = resources["Orders.OrderTime"];
            var received = resources["Orders.ReceivedAt"];
            using var client = await SignedInClientAsync(host, name, ct);

            var orders = await PageAsync(client, host, $"/orders?startDate={yesterday:yyyy-MM-dd}&endDate={today:yyyy-MM-dd}", ct);
            Assert.Contains($"dir=\"{(name == "ar-SA" ? "rtl" : "ltr")}\"", orders, StringComparison.Ordinal);
            Assert.Contains($"sortBy={OrderListSort.OrderedAt}", orders, StringComparison.Ordinal);
            Assert.Contains(orderTime, VisibleText(Section(orders, "<thead>")), StringComparison.Ordinal);

            var recoveryRow = Row(orders, recoveryId);
            Assert.StartsWith(placedUtc.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), FirstTimeAttribute(recoveryRow), StringComparison.Ordinal);
            Assert.Contains(received, VisibleText(recoveryRow), StringComparison.Ordinal);

            var fallbackRow = Row(orders, fallbackId);
            Assert.Contains(received, VisibleText(fallbackRow), StringComparison.Ordinal);
            Assert.Contains($"title=\"{resources["Orders.OrderTimeFallbackHint"]}\"", fallbackRow, StringComparison.Ordinal);

            var dashboard = await PageAsync(client, host, "/dashboard", ct);
            var recent = Section(dashboard, "data-tour=\"dashboard-recent\"");
            Assert.Contains(orderTime, recent, StringComparison.Ordinal);

            var details = VisibleText(await PageAsync(client, host, $"/orders/details/{recoveryId}", ct));
            foreach (var key in new[] { "Orders.OrderTime", "Orders.Details.PlacedOnPlatform", "Orders.Details.Received", "Orders.Details.LastChangedInWasla" })
                Assert.True(details.Contains(resources[key], StringComparison.Ordinal), $"{name} details: no {key}");
            Assert.DoesNotContain(resources["Orders.Details.NotProvidedByPlatform"], details, StringComparison.Ordinal);

            var fallbackDetails = VisibleText(await PageAsync(client, host, $"/orders/details/{fallbackId}", ct));
            Assert.Contains(resources["Orders.Details.NotProvidedByPlatform"], fallbackDetails, StringComparison.Ordinal);
        }

        Assert.Null(host.ServerErrors);
    }

    [Theory]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void OrderTimeResources_ExistInEveryCulture_AndTheImportLabelNamesWasla(string culture)
    {
        var resources = Resources(culture);

        foreach (var key in new[]
                 {
                     "Orders.OrderTime", "Orders.ReceivedAt", "Orders.OrderTimeFallbackHint", "Orders.Details.Received",
                     "Orders.Details.PlacedOnPlatform", "Orders.Details.PlatformModified", "Orders.Details.LastChangedInWasla",
                     "Orders.Details.NotProvidedByPlatform"
                 })
            Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{key} missing in SharedResource{culture}.resx");

        // The import label can no longer be read as the order time.
        Assert.Contains("Wasla", resources["Orders.ReceivedAt"], StringComparison.Ordinal);
        Assert.Contains("Wasla", resources["Orders.Details.Received"], StringComparison.Ordinal);
        Assert.NotEqual(resources["Orders.OrderTime"], resources["Orders.ReceivedAt"]);
        Assert.DoesNotContain("Dashboard.Received", resources.Keys);
    }

    /// <summary>
    /// The Türkiye-local day that holds <paramref name="nowUtc"/>, and an instant of that day no later than
    /// <paramref name="nowUtc"/>: <paramref name="preferredUtc"/> when it is in that day, otherwise the day's first moment.
    /// </summary>
    private static (DateOnly Today, DateTime InstantUtc) TurkeyTodayAt(DateTime nowUtc, DateTime preferredUtc)
    {
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), tz);
        return (today, preferredUtc < startUtc ? startUtc : preferredUtc);
    }

    private static async Task<int> TodayOrderCountAsync(TenantVisualWebHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var dashboard = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        return (await dashboard.GetTodayAsync(TenantVisualWebHost.TenantId, ct)).TodayOrderCount;
    }

    private static async Task<Guid> SeedAsync(TenantVisualWebHost host, string code, DateTime? placedUtc, DateTime receivedUtc, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var factory = host.Services.GetRequiredService<ITenantDbContextFactory>();
        await using var db = await factory.CreateAsync(TenantVisualWebHost.TenantId, ct);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = $"{FoodPlatform.TrendyolYemek}:{code}",
            InternalStatus = OrderStatus.Delivered,
            PlatformStatus = "Delivered",
            CustomerName = "Zaman Testi",
            TotalAmount = 120m,
            PaymentMethod = PaymentMethod.OnlinePayment,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = placedUtc,
            PlatformModifiedAt = placedUtc?.AddMinutes(50),
            ReceivedAt = receivedUtc,
            DeliveredAt = receivedUtc,
            CreatedAt = receivedUtc,
            UpdatedAt = receivedUtc,
            RawPayloadJson = "{}",
            Items = [new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 120m, TotalPrice = 120m }]
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    private static string Row(string html, Guid orderId)
    {
        var start = html.IndexOf($"<tr data-order-id=\"{orderId}\"", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"row {orderId} not found");
        var end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static string FirstTimeAttribute(string html)
    {
        const string marker = "<time datetime=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "no <time> element");
        start += marker.Length;
        return html[start..html.IndexOf('"', start)];
    }
}
