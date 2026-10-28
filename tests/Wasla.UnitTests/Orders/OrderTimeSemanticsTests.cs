using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Orders;
using Wasla.Application.Platform.Dtos;
using Wasla.Application.Time;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Diagnostics;
using Wasla.Web.Ui;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-125: an order's times keep their meaning from ingestion to the screens. The provider's creation time is the
/// order time; Wasla's first-import time is a separate fact that synchronization never moves; without a plausible
/// provider time the import time stands in, and is never stored as the provider's. Uses the real tenant model on SQLite,
/// so the stored <c>OrderedAt</c> column is the one the migration creates.
/// </summary>
public sealed class OrderTimeSemanticsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The production incident's shape, in UTC: placed 20:40 on 16 October in Turkey, imported by recovery at 02:19 on 17
    // October in Turkey (23:19 UTC on 16 October).
    private static readonly DateTime RecoveryPlacedUtc = new(2026, 10, 16, 17, 40, 0, DateTimeKind.Utc);
    private static readonly DateTime RecoveryImportedUtc = new(2026, 10, 16, 23, 19, 0, DateTimeKind.Utc);

    private readonly RecordingTenantDbFactory _db = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly FakeClient _client = new();
    private readonly CollectingLogger<OrderSyncService> _syncLog = new();
    private bool _connectionSeeded;

    public OrderTimeSemanticsTests() => _db.CreateTenantDatabase(_tenantId);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task FreshOrder_StoresTheProviderTimes_AndTheImportTimeSeparately()
    {
        var placed = DateTime.UtcNow.AddMinutes(-1);
        var modified = placed.AddSeconds(20);
        var before = DateTime.UtcNow;

        var result = await SyncAsync(Sample("fresh", placed, modified));

        Assert.Equal(1, result.InsertedCount);
        var stored = await ReadAsync("fresh");
        Assert.Equal(placed, stored.CreatedAtPlatform);
        Assert.Equal(modified, stored.PlatformModifiedAt);
        Assert.InRange(stored.ReceivedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(placed, stored.OrderedAt);
    }

    [Fact]
    public async Task RecoveryImport_ShowsThePlacedTimeAsTheOrderTime_NotTheImportTime()
    {
        var placed = DateTime.UtcNow.AddHours(-7);

        await SyncAsync(Sample("recovered", placed, placed.AddMinutes(50), status: "Delivered"));

        var stored = await ReadAsync("recovered");
        Assert.Equal(placed, stored.OrderedAt);
        Assert.True(stored.ReceivedAt - placed > TimeSpan.FromHours(6), "the import time stays the import time");

        var row = Assert.Single((await Reader().GetListAsync(_tenantId, null, null, null, null, null, null, 1, 25, null, Ct)).Items);
        Assert.Equal(placed, row.OrderedAtUtc);
        Assert.Equal(placed, row.CreatedAtPlatformUtc);
        Assert.Equal(stored.ReceivedAt, row.ReceivedAtUtc);
        Assert.True(OrderUiHelper.ShowReceivedSeparately(row.CreatedAtPlatformUtc, row.ReceivedAtUtc));
    }

    [Fact]
    public async Task RepeatedSync_NeverMovesTheImportTime_OrForgetsTheProviderTime()
    {
        var placed = DateTime.UtcNow.AddMinutes(-3);
        await SyncAsync(Sample("repeat", placed, placed));
        var first = await ReadAsync("repeat");

        var unchanged = await SyncAsync(Sample("repeat", placed, placed));
        Assert.Equal(1, unchanged.UnchangedCount);

        var updated = await SyncAsync(Sample("repeat", placed, placed.AddMinutes(2), status: "Picking"));
        Assert.Equal(1, updated.UpdatedCount);

        // A later payload without times (as the mock's updates send) keeps the stored provider times.
        var withoutTimes = await SyncAsync(Sample("repeat", null, null, status: "Picking"));
        Assert.Equal(1, withoutTimes.UnchangedCount);

        var last = await ReadAsync("repeat");
        Assert.Equal(first.ReceivedAt, last.ReceivedAt);
        Assert.Equal(placed, last.CreatedAtPlatform);
        Assert.Equal(placed.AddMinutes(2), last.PlatformModifiedAt);
        Assert.Equal(placed, last.OrderedAt);
    }

    [Fact]
    public async Task ProviderModificationTime_AloneIsAChange()
    {
        var placed = DateTime.UtcNow.AddMinutes(-3);
        await SyncAsync(Sample("modified", placed, placed));

        var result = await SyncAsync(Sample("modified", placed, placed.AddMinutes(1)));

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(placed.AddMinutes(1), (await ReadAsync("modified")).PlatformModifiedAt);
    }

    [Fact]
    public async Task MissingProviderTime_FallsBackToTheImportTime_AndIsNotStoredAsTheProviderTime()
    {
        await SyncAsync(Sample("no-time", null, null));

        var stored = await ReadAsync("no-time");
        Assert.Null(stored.CreatedAtPlatform);
        Assert.Null(stored.PlatformModifiedAt);
        Assert.Equal(stored.ReceivedAt, stored.OrderedAt);

        var detail = await Reader().GetByIdAsync(_tenantId, stored.Id, Ct);
        Assert.NotNull(detail);
        Assert.Null(detail.CreatedAtPlatformUtc);
        Assert.Equal(detail.ReceivedAtUtc, detail.OrderedAtUtc);
        Assert.False(OrderUiHelper.ShowReceivedSeparately(detail.CreatedAtPlatformUtc, detail.ReceivedAtUtc));
    }

    public static TheoryData<string, DateTime> ImplausibleTimes => new()
    {
        { "epoch-zero", DateTimeOffset.FromUnixTimeMilliseconds(0).UtcDateTime },
        { "far-future", DateTime.UtcNow.AddDays(1) }
    };

    [Theory]
    [MemberData(nameof(ImplausibleTimes))]
    public async Task ImplausibleProviderTime_IsIgnored_AndLoggedWithoutCustomerData(string id, DateTime value)
    {
        await SyncAsync(Sample(id, value, value));

        var stored = await ReadAsync(id);
        Assert.Null(stored.CreatedAtPlatform);
        Assert.Null(stored.PlatformModifiedAt);
        Assert.Equal(stored.ReceivedAt, stored.OrderedAt);

        var warning = Assert.Single(_syncLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("implausible", StringComparison.Ordinal));
        Assert.Contains(id, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ayse", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("5313241245", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderClockSkew_WithinTheToleranceIsAccepted()
    {
        var now = new DateTime(2026, 10, 17, 6, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddMinutes(4), OrderIngestion.PlausibleProviderTime(now.AddMinutes(4), now));
        Assert.Null(OrderIngestion.PlausibleProviderTime(now.AddMinutes(6), now));
        Assert.Null(OrderIngestion.PlausibleProviderTime(new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc), now));
        Assert.Equal(DateTimeKind.Utc, OrderIngestion.PlausibleProviderTime(DateTime.SpecifyKind(now, DateTimeKind.Unspecified), now)!.Value.Kind);
    }

    [Fact]
    public async Task OrdersList_FiltersTurkeyLocalDaysAndSortsByOrderTime_ImportTimeOnRequest()
    {
        var recovery = SeedOrder("TY-RECOVERY", RecoveryPlacedUtc, RecoveryImportedUtc);
        // Placed 02:00 on 17 October in Turkey, imported half a minute later.
        var live = SeedOrder("TY-LIVE", new DateTime(2026, 10, 16, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 16, 23, 0, 30, DateTimeKind.Utc));
        // Stored before WAS-125 or by a provider without a creation time: 23:00 on 16 October in Turkey.
        var legacy = SeedOrder("TY-LEGACY", null, new DateTime(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc));

        Assert.Equal([legacy, recovery], await ListAsync(new DateOnly(2026, 10, 16), sortBy: null));
        Assert.Equal([live], await ListAsync(new DateOnly(2026, 10, 17), sortBy: null));

        Assert.Equal([live, legacy, recovery], await ListAsync(null, sortBy: null));
        Assert.Equal([live, legacy, recovery], await ListAsync(null, sortBy: OrderListSort.OrderedAt));
        Assert.Equal([recovery, live, legacy], await ListAsync(null, sortBy: OrderListSort.ReceivedAt));
        Assert.Equal([recovery, legacy, live], await ListAsync(null, sortBy: OrderListSort.OrderedAt, sortDirection: "asc"));
    }

    [Fact]
    public async Task LiveScreen_CountsTodayAndSortsByOrderTime_AndSendsOnlyTheProviderTimeAsPlaced()
    {
        // 09:00 on 17 October in Turkey.
        var now = new DateTime(2026, 10, 17, 6, 0, 0, DateTimeKind.Utc);
        var recovery = SeedOrder("TY-RECOVERY", RecoveryPlacedUtc, RecoveryImportedUtc);
        var live = SeedOrder("TY-LIVE", now.AddMinutes(-10), now.AddMinutes(-10).AddSeconds(30));
        var legacy = SeedOrder("TY-LEGACY", null, now.AddHours(-1));

        var reader = new OrderReadService(_db, new FixedTime(now), NullLogger<OrderReadService>.Instance);
        var snapshot = await reader.GetLiveScreenSnapshotAsync(_tenantId, Ct);

        // Imported at 02:19 today, placed yesterday evening: not one of today's orders.
        Assert.Equal(2, snapshot.TodayOrderCount);
        Assert.Equal([live, legacy, recovery], snapshot.Orders.Select(o => o.Id).ToArray());

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var orders = document.RootElement.GetProperty("orders").EnumerateArray().ToDictionary(o => o.GetProperty("displayNumber").GetString()!);
        Assert.Equal(RecoveryPlacedUtc, orders["TY-RECOVERY"].GetProperty("placedAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(RecoveryImportedUtc, orders["TY-RECOVERY"].GetProperty("receivedAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, orders["TY-LEGACY"].GetProperty("placedAtUtc").ValueKind);
        Assert.False(orders["TY-LIVE"].TryGetProperty("orderedAtUtc", out _));
    }

    [Theory]
    [InlineData("tr-TR", "16.10.2026 20:40")]
    [InlineData("en-US", "10/16/2026 8:40 PM")]
    public void Display_ConvertsTheStoredUtcInstantToTurkeyTimeOnce(string culture, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var tz = TimeZoneHelper.ResolveTurkeyTimeZone();

            // Yesterday's order shows date and time; the server's own zone plays no part.
            var label = OrderUiHelper.FormatReceivedAtUtc(RecoveryPlacedUtc, tz, new DateOnly(2026, 10, 17));
            Assert.Equal(expected, label.Replace(' ', ' '));

            // The recovery import was received at 02:19 on the 17th, which is "today" there and shows the time only.
            var imported = OrderUiHelper.FormatReceivedAtUtc(RecoveryImportedUtc, tz, new DateOnly(2026, 10, 17));
            Assert.DoesNotContain("2026", imported, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ImportTimeIsShownSeparately_OnlyForAMuchLaterImportOfAProviderTimedOrder()
    {
        Assert.True(OrderUiHelper.ShowReceivedSeparately(RecoveryPlacedUtc, RecoveryImportedUtc));
        Assert.False(OrderUiHelper.ShowReceivedSeparately(RecoveryImportedUtc.AddSeconds(-30), RecoveryImportedUtc));
        Assert.False(OrderUiHelper.ShowReceivedSeparately(RecoveryImportedUtc - OrderUiHelper.ReceivedSeparatelyAfter.Add(TimeSpan.FromSeconds(-1)), RecoveryImportedUtc));
        Assert.True(OrderUiHelper.ShowReceivedSeparately(RecoveryImportedUtc - OrderUiHelper.ReceivedSeparatelyAfter, RecoveryImportedUtc));
        Assert.False(OrderUiHelper.ShowReceivedSeparately(null, RecoveryImportedUtc));
    }

    private async Task<Guid[]> ListAsync(DateOnly? localDay, string? sortBy, string? sortDirection = null)
    {
        OrdersReceivedAtQueryRange.MapLocalDatesToUtcRange(localDay, localDay, out var start, out var end);
        var result = await Reader().GetListAsync(_tenantId, null, null, start, end, sortBy, sortDirection, 1, 25, null, Ct);
        return result.Items.Select(row => row.Id).ToArray();
    }

    private OrderReadService Reader() => new(_db, TimeProvider.System, NullLogger<OrderReadService>.Instance);

    private Guid SeedOrder(string code, DateTime? placedUtc, DateTime receivedUtc)
    {
        var id = Guid.NewGuid();
        _db.Seed(_tenantId, db => db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = $"{code}:{_tenantId:N}",
            InternalStatus = OrderStatus.New,
            PlatformStatus = "Created",
            CustomerName = "Test Customer",
            TotalAmount = 100m,
            CreatedAtPlatform = placedUtc,
            ReceivedAt = receivedUtc,
            RawPayloadJson = "{}",
            CreatedAt = receivedUtc,
            UpdatedAt = receivedUtc
        }));
        return id;
    }

    private async Task<OrderSyncCustomerResult> SyncAsync(ExternalOrderDto order)
    {
        if (!_connectionSeeded)
        {
            _db.Seed(_tenantId, db =>
            {
                db.PlatformConnections.Add(new PlatformConnection
                {
                    Platform = FoodPlatform.TrendyolYemek,
                    StoreId = "store-1",
                    EncryptedApiKey = "encrypted",
                    EncryptedApiSecret = "encrypted",
                    IsActive = true,
                    SyncIntervalSeconds = 0
                });
                db.TenantOperationalSettings.Add(new TenantOperationalSettings
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    OrderSyncEnabled = true
                });
            });
            _connectionSeeded = true;
        }

        var sync = new OrderSyncService(
            _db,
            [_client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            NullTenantChangeNotifier.Instance,
            _syncLog);
        _client.Set(order);
        return await sync.SyncCustomerWithResultAsync(_tenantId, Ct);
    }

    private async Task<Order> ReadAsync(string externalOrderId)
    {
        await using var db = await _db.CreateAsync(_tenantId, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
    }

    private static ExternalOrderDto Sample(string id, DateTime? placedUtc, DateTime? modifiedUtc, string status = "Created") =>
        new(
            FoodPlatform.TrendyolYemek,
            id,
            "TY-" + id,
            placedUtc,
            "Ayse Yilmaz",
            "5313241245",
            "Istiklal Cad. No:1",
            100m,
            0m,
            0m,
            100m,
            PaymentMethod.OnlinePayment,
            PaymentStatus.Paid,
            status,
            "{}",
            [new ExternalOrderItemDto(id + "-item", "Lahmacun", 1, 100m, 100m, null, [])],
            ModifiedAtUtc: modifiedUtc);

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FakeClient : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(_orders);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
