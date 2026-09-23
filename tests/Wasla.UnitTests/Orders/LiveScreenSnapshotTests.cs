using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// Behavioral coverage for the Live Screen snapshot.
/// The SQLite model exercises the query; it does not prove SQL Server translation or the HTTP pipeline.
/// </summary>
public sealed class LiveScreenSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TenantSqlite _databases = new();
    private readonly FixedTimeProvider _clock = new(Now);

    [Fact]
    public async Task ActiveOrder_ReceivedYesterday_IsIncluded()
    {
        var id = await SeedAsync(_tenantId, OrderStatus.Preparing, NowUtc.AddDays(-1));

        var snapshot = await ReadAsync(_tenantId);

        Assert.Equal(NowUtc, snapshot.ServerTimeUtc);
        Assert.Contains(snapshot.Orders, order => order.Id == id);
    }

    [Fact]
    public async Task MoreThanOneHundredActiveOrders_AreReturnedWithoutTruncation()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 101; i++)
            ids.Add(await SeedAsync(_tenantId, OrderStatus.New, NowUtc.AddMinutes(-i), code: $"TY-{i:000}"));

        await SeedAsync(_tenantId, OrderStatus.Cancelled, NowUtc, code: "TY-CANCELLED");

        var snapshot = await ReadAsync(_tenantId);

        Assert.Equal(101, snapshot.Orders.Count);
        Assert.Equal(ids.OrderBy(id => id).ToArray(), snapshot.Orders.Select(order => order.Id).OrderBy(id => id).ToArray());
        Assert.DoesNotContain(snapshot.Orders, order => order.DisplayNumber == "TY-CANCELLED");
    }

    [Fact]
    public async Task DeliveredOrders_RespectTheInclusiveTwoMinuteBoundary()
    {
        var inside = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddHours(-3), NowUtc.AddSeconds(-90), "TY-INSIDE");
        var exact = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddHours(-3), NowUtc.AddMinutes(-2), "TY-EXACT");
        var outside = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddHours(-3), NowUtc.AddMinutes(-2).AddSeconds(-1), "TY-OUTSIDE");
        var future = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddHours(-3), NowUtc.AddMinutes(1), "TY-FUTURE");
        var atNow = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddHours(-3), NowUtc, "TY-NOW");

        var snapshot = await ReadAsync(_tenantId);
        var ids = snapshot.Orders.Select(order => order.Id).ToHashSet();

        Assert.Equal(NowUtc, snapshot.ServerTimeUtc);
        Assert.Contains(inside, ids);
        Assert.Contains(exact, ids);
        Assert.Contains(atNow, ids);
        Assert.DoesNotContain(outside, ids);
        Assert.DoesNotContain(future, ids);
    }

    [Fact]
    public async Task DeliveredOrder_WithMissingDeliveredAt_IsExcluded()
    {
        var missing = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddMinutes(-1), deliveredAt: null, code: "TY-NO-TIME");
        var known = await SeedAsync(_tenantId, OrderStatus.Delivered, NowUtc.AddMinutes(-1), NowUtc.AddSeconds(-30), "TY-KNOWN");

        var snapshot = await ReadAsync(_tenantId);

        Assert.DoesNotContain(snapshot.Orders, order => order.Id == missing);
        Assert.Contains(snapshot.Orders, order => order.Id == known && order.DeliveredAtUtc == NowUtc.AddSeconds(-30));
    }

    [Fact]
    public async Task CancelledAndFailedOrders_AreExcluded()
    {
        await SeedAsync(_tenantId, OrderStatus.Cancelled, NowUtc, code: "TY-CANCELLED");
        await SeedAsync(_tenantId, OrderStatus.Failed, NowUtc, code: "TY-FAILED");
        var active = await SeedAsync(_tenantId, OrderStatus.OnTheWay, NowUtc.AddDays(-2), code: "TY-ACTIVE");

        var snapshot = await ReadAsync(_tenantId);

        Assert.Equal([active], snapshot.Orders.Select(order => order.Id).ToArray());
    }

    [Fact]
    public async Task Snapshot_ReturnsOnlyTheRequestedTenant()
    {
        var otherTenantId = Guid.NewGuid();
        var own = await SeedAsync(_tenantId, OrderStatus.New, NowUtc, code: "TY-OWN");
        await SeedAsync(otherTenantId, OrderStatus.New, NowUtc, code: "TY-OTHER");

        var snapshot = await ReadAsync(_tenantId);

        Assert.Equal([own], snapshot.Orders.Select(order => order.Id).ToArray());
    }

    [Fact]
    public async Task Snapshot_PreservesItemNotesAndRealStatuses()
    {
        var accepted = await SeedAsync(
            _tenantId,
            OrderStatus.Accepted,
            NowUtc.AddMinutes(-10),
            code: "TY-ACCEPTED",
            items:
            [
                new ItemSeed(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Ayran", 2, null),
                new ItemSeed(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Lahmacun", 1, "No onion")
            ]);
        await SeedAsync(_tenantId, OrderStatus.New, NowUtc, code: "TY-NEW");
        await SeedAsync(_tenantId, OrderStatus.Preparing, NowUtc, code: "TY-PREPARING");
        await SeedAsync(_tenantId, OrderStatus.ReadyForPickup, NowUtc, code: "TY-READY");
        await SeedAsync(_tenantId, OrderStatus.OnTheWay, NowUtc, code: "TY-WAY");

        var before = await CaptureAsync(_tenantId);
        var snapshot = await ReadAsync(_tenantId);
        var after = await CaptureAsync(_tenantId);

        Assert.Equal(before, after);
        Assert.Equal(
            [OrderStatus.New, OrderStatus.Accepted, OrderStatus.Preparing, OrderStatus.ReadyForPickup, OrderStatus.OnTheWay],
            snapshot.Orders.Select(order => order.Status).ToArray());

        var card = snapshot.Orders.Single(order => order.Id == accepted);
        Assert.Equal(OrderStatus.Accepted, card.Status);
        Assert.Equal(
            [("Lahmacun", 1, "No onion"), ("Ayran", 2, null)],
            card.Items.Select(item => (item.ProductName, item.Quantity, item.Notes)).ToArray());
    }

    [Fact]
    public async Task Snapshot_OrdersByStatusThenReceivedTimeThenId()
    {
        var olderNew = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var newerNew = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var tiedLower = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var tiedHigher = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var preparing = Guid.Parse("00000000-0000-0000-0000-000000000030");

        await SeedAsync(_tenantId, OrderStatus.New, NowUtc.AddHours(-2), code: "TY-OLDER", id: olderNew);
        await SeedAsync(_tenantId, OrderStatus.New, NowUtc.AddHours(-1), code: "TY-NEWER", id: newerNew);
        await SeedAsync(_tenantId, OrderStatus.New, NowUtc.AddHours(-3), code: "TY-TIE-HIGH", id: tiedHigher);
        await SeedAsync(_tenantId, OrderStatus.New, NowUtc.AddHours(-3), code: "TY-TIE-LOW", id: tiedLower);
        await SeedAsync(_tenantId, OrderStatus.Preparing, NowUtc, code: "TY-PREP", id: preparing);

        var snapshot = await ReadAsync(_tenantId);

        Assert.Equal([newerNew, olderNew, tiedLower, tiedHigher, preparing], snapshot.Orders.Select(order => order.Id).ToArray());
    }

    [Fact]
    public async Task SnapshotJson_UsesTheOperationalShapeAndOmitsSensitiveFields()
    {
        await SeedAsync(
            _tenantId,
            OrderStatus.Accepted,
            NowUtc.AddMinutes(-5),
            code: "TY-42",
            phone: "+905551112233",
            address: "Secret Street 7",
            rawPayload: "{\"secret\":\"platform-payload\"}",
            items: [new ItemSeed(Guid.NewGuid(), "Lahmacun", 1, "No onion")]);

        var snapshot = await ReadAsync(_tenantId);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var order = document.RootElement.GetProperty("orders")[0];

        Assert.Equal(1, document.RootElement.GetProperty("todayOrderCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("cancelledOrderCount").GetInt32());
        Assert.Equal(NowUtc, document.RootElement.GetProperty("serverTimeUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("TY-42", order.GetProperty("displayNumber").GetString());
        Assert.Equal("TrendyolYemek", order.GetProperty("platform").GetString());
        Assert.Equal("Accepted", order.GetProperty("status").GetString());
        Assert.Equal("Lahmacun", order.GetProperty("items")[0].GetProperty("productName").GetString());
        Assert.Equal(1, order.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal("No onion", order.GetProperty("items")[0].GetProperty("notes").GetString());
        Assert.False(order.TryGetProperty("customerPhone", out _));
        Assert.False(order.TryGetProperty("customerAddress", out _));
        Assert.False(order.TryGetProperty("rawPayloadJson", out _));
        Assert.False(order.TryGetProperty("printJob", out _));
        Assert.DoesNotContain("+905551112233", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Street 7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("platform-payload", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DayCounts_UseTurkeyLocalReceivedAtAndStayOutOfTheActiveList()
    {
        var yesterdayActive = await SeedAsync(_tenantId, OrderStatus.New, NowUtc.AddDays(-1), code: "TY-YESTERDAY");
        var todayActive = await SeedAsync(_tenantId, OrderStatus.Accepted, new DateTime(2026, 9, 22, 21, 0, 0, DateTimeKind.Utc), code: "TY-TODAY");
        await SeedAsync(_tenantId, OrderStatus.Cancelled, NowUtc.AddHours(-1), code: "TY-CANCEL-TODAY");
        await SeedAsync(_tenantId, OrderStatus.Cancelled, NowUtc.AddDays(-1), code: "TY-CANCEL-YESTERDAY");
        await SeedAsync(
            _tenantId,
            OrderStatus.Delivered,
            new DateTime(2026, 9, 23, 1, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 23, 1, 0, 0, DateTimeKind.Utc),
            "TY-DELIVERED-TODAY");

        var snapshot = await ReadAsync(_tenantId);
        var ids = snapshot.Orders.Select(order => order.Id).ToHashSet();

        Assert.Equal(3, snapshot.TodayOrderCount);
        Assert.Equal(1, snapshot.CancelledOrderCount);
        Assert.Contains(yesterdayActive, ids);
        Assert.Contains(todayActive, ids);
        Assert.DoesNotContain(snapshot.Orders, order => order.DisplayNumber.StartsWith("TY-CANCEL", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Orders, order => order.DisplayNumber == "TY-DELIVERED-TODAY");
    }

    [Fact]
    public async Task LiveData_ReadsTheAuthenticatedTenantAndPropagatesReadFailures()
    {
        var tenantId = Guid.NewGuid();
        var orders = new RecordingOrders();
        var controller = CreateController(new FixedTenant(new ResolvedTenantDto(tenantId, "Sushi M", "sushi-m", "sushi-m.wasla.local")), orders);

        var result = await controller.LiveData(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(orders.Snapshot, ok.Value);
        Assert.Equal(tenantId, orders.RequestedTenantId);

        var missingTenant = CreateController(new FixedTenant(null), orders);
        Assert.IsType<NotFoundResult>(await missingTenant.LiveData(CancellationToken.None));

        orders.Failure = new InvalidOperationException("database unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.LiveData(CancellationToken.None));
    }

    [Fact]
    public void LiveData_DeclaresLiveScreenAuthorizationAndNoStore()
    {
        // This inspects attributes only. It does not execute the authorization middleware.
        // Role mapping for CanViewLiveScreen is covered by TenantRolesAuthorizationTests.
        var method = typeof(OrdersController).GetMethod(nameof(OrdersController.LiveData));
        Assert.NotNull(method);

        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(TenantPolicies.CanViewLiveScreen, authorize?.Policy);

        var route = typeof(OrdersController).GetCustomAttribute<RouteAttribute>();
        var get = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.Equal("orders", route?.Template);
        Assert.Equal("live-data", get?.Template);

        var cache = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cache);
        Assert.True(cache!.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);

        Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.Name?.Contains("tenant", StringComparison.OrdinalIgnoreCase) == true);
    }

    public void Dispose() => _databases.Dispose();

    private Task<LiveScreenSnapshotResult> ReadAsync(Guid tenantId)
    {
        var reader = new OrderReadService(_databases, _clock, NullLogger<OrderReadService>.Instance);
        return reader.GetLiveScreenSnapshotAsync(tenantId, CancellationToken.None);
    }

    private async Task<Guid> SeedAsync(
        Guid tenantId,
        OrderStatus status,
        DateTime receivedAt,
        DateTime? deliveredAt = null,
        string code = "TY-1",
        Guid? id = null,
        string phone = "+905550000000",
        string address = "Test Address",
        string rawPayload = "{}",
        IReadOnlyList<ItemSeed>? items = null)
    {
        var orderId = id ?? Guid.NewGuid();
        await using var db = await _databases.CreateAsync(tenantId, CancellationToken.None);
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = $"{code}:{tenantId:N}",
            InternalStatus = status,
            PlatformStatus = "Created",
            CustomerName = "Test Customer",
            CustomerPhone = phone,
            CustomerAddress = address,
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = receivedAt,
            ReceivedAt = receivedAt,
            DeliveredAt = deliveredAt,
            RawPayloadJson = rawPayload,
            CreatedAt = NowUtc,
            UpdatedAt = NowUtc,
            Items = (items ?? [new ItemSeed(Guid.NewGuid(), "Lahmacun", 1, null)])
                .Select(item => new OrderItem
                {
                    Id = item.Id,
                    ProductName = item.ProductName,
                    Quantity = item.Quantity,
                    UnitPrice = 100m,
                    TotalPrice = 100m,
                    Notes = item.Notes,
                    CreatedAt = NowUtc,
                    UpdatedAt = NowUtc
                })
                .ToList()
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private async Task<string> CaptureAsync(Guid tenantId)
    {
        await using var db = await _databases.CreateAsync(tenantId, CancellationToken.None);
        var rows = await db.Orders.AsNoTracking()
            .OrderBy(order => order.Id)
            .Select(order => new { order.Id, order.InternalStatus, order.UpdatedAt, order.DeliveredAt })
            .ToListAsync();
        return JsonSerializer.Serialize(rows);
    }

    private static OrdersController CreateController(ICurrentTenantService tenant, IOrderReadService orders) =>
        new(
            tenant,
            orders,
            actions: null!,
            orderSyncSettings: null!,
            orderSettings: null!,
            receiptCreation: null!,
            manualPrint: null!,
            orderSettingsValidator: null!,
            logger: null!,
            localizer: null!);

    private sealed record ItemSeed(Guid Id, string ProductName, int Quantity, string? Notes);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FixedTenant : ICurrentTenantService
    {
        public FixedTenant(ResolvedTenantDto? tenant) => CurrentTenant = tenant;

        public ResolvedTenantDto? CurrentTenant { get; }
    }

    private sealed class RecordingOrders : IOrderReadService
    {
        public Guid? RequestedTenantId { get; private set; }
        public Exception? Failure { get; set; }
        public LiveScreenSnapshotResult Snapshot { get; } = new(NowUtc, [], 0, 0);

        public Task<LiveScreenSnapshotResult> GetLiveScreenSnapshotAsync(Guid customerId, CancellationToken ct)
        {
            RequestedTenantId = customerId;
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(Snapshot);
        }

        public Task<OrderListResult> GetListAsync(
            Guid customerId,
            FoodPlatform? platform,
            OrderStatus? status,
            DateTime? startDateUtc,
            DateTime? endDateUtc,
            string? sortBy,
            string? sortDirection,
            int page,
            int pageSize,
            string? search,
            CancellationToken ct,
            bool includeLineItems = false) =>
            throw new NotSupportedException();

        public Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(customerId, connection);
                using var setup = new TestTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new TestTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class TestTenantDbContext : TenantDbContext
    {
        public TestTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(builder =>
            {
                builder.ToTable("Orders");
                builder.HasKey(x => x.Id);
                builder.HasIndex(x => x.IdempotencyKey).IsUnique();
                builder.HasMany(x => x.Items).WithOne(x => x.Order).HasForeignKey(x => x.OrderId);
            });

            modelBuilder.Entity<OrderItem>(builder =>
            {
                builder.ToTable("OrderItems");
                builder.HasKey(x => x.Id);
            });
        }
    }
}
