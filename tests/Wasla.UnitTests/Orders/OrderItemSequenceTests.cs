using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-44: an order's items and their options keep the order of the provider's list on every surface that shows them
/// (order details, the Live Screen snapshot, the list's line items, a receipt and its reprint). Rows stored before the
/// position was kept fall back to their stored time, then their Id, and every surface still agrees. Runs the real sync,
/// read and receipt services over the real tenant model on SQLite; it does not prove SQL Server translation.
/// </summary>
public sealed class OrderItemSequenceTests : IDisposable
{
    private static readonly DateTime OrderedAt = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    // Neither alphabetical nor any other order a database or GUID sort would produce by chance (8! orders).
    private static readonly string[] ProviderItems =
        ["Mercimek Çorbası", "Adana Kebap", "Ayran", "Künefe", "Lahmacun", "Çoban Salata", "Baklava", "Su"];

    private static readonly string[] ProviderOptions = ["Acılı", "Ekstra lavaş", "Az pişmiş", "Bol soğan", "Yoğurtlu"];

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TenantSqlite _databases = new();
    private readonly FakeClient _client = new();

    public void Dispose() => _databases.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewOrder_KeepsTheProviderSequence_OnEverySurface()
    {
        var sync = await CreateSyncAsync();
        await SyncAsync(sync, Sample("ext-1", ProviderItems));
        var orderId = await OrderIdAsync("ext-1");

        await AssertEverySurfaceShowsAsync(orderId, ProviderItems, ProviderOptions);
    }

    [Fact]
    public async Task ProviderUpdate_StoresTheNewSequence()
    {
        var sync = await CreateSyncAsync();
        await SyncAsync(sync, Sample("ext-2", ["Ayran", "Adana Kebap", "Künefe"]));

        // A content change (a new line) rewrites the items in the provider's new order.
        string[] updated = ["Künefe", "Su", "Adana Kebap", "Ayran"];
        var result = await SyncAsync(sync, Sample("ext-2", updated, rawPayloadJson: """{"id":"ext-2","v":2}"""));
        Assert.Equal(1, result.UpdatedCount);

        await AssertEverySurfaceShowsAsync(await OrderIdAsync("ext-2"), updated, ProviderOptions);
    }

    /// <summary>
    /// Sync compares items as a set and writes nothing when only their order changed (no write, no live notification).
    /// The stored sequence is then the provider's order at the last write. Documents that limit.
    /// </summary>
    [Fact]
    public async Task ProviderReorderWithoutAnyOtherChange_IsNotWritten()
    {
        var sync = await CreateSyncAsync();
        string[] first = ["Ayran", "Adana Kebap", "Künefe"];
        await SyncAsync(sync, Sample("ext-3", first));

        // Same lines, same options on the same line; only their order differs.
        var result = await SyncAsync(sync, Sample("ext-3", ["Künefe", "Ayran", "Adana Kebap"], optionsOn: "Ayran"));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.UnchangedCount);
        await AssertEverySurfaceShowsAsync(await OrderIdAsync("ext-3"), first, ProviderOptions);
    }

    [Fact]
    public async Task RowsStoredWithoutAPosition_FallBackToStoredTimeThenId_TheSameOnEverySurface()
    {
        var stored = DateTime.UtcNow.AddMinutes(-5);
        var orderId = Guid.NewGuid();
        await using (var db = await _databases.CreateAsync(_tenantId, Ct))
        {
            var order = NewOrder(orderId, "legacy-1");
            // As written before positions existed: every position is the default 0. Three items share one stored time
            // (the sync update path wrote them so), the others were stored later in a known order.
            order.Items.Add(LegacyItem("Tie A", stored, ["x", "y"]));
            order.Items.Add(LegacyItem("Third", stored.AddMilliseconds(30), []));
            order.Items.Add(LegacyItem("Tie B", stored, []));
            order.Items.Add(LegacyItem("Second", stored.AddMilliseconds(20), []));
            order.Items.Add(LegacyItem("Tie C", stored, []));
            order.Items.Add(LegacyItem("First", stored.AddMilliseconds(10), []));
            db.Orders.Add(order);
            await db.SaveChangesAsync(Ct);
        }

        var surfaces = await ReadSurfacesAsync(orderId);
        var details = surfaces.Details;

        // Stored time first; the three that share it come before the later ones, in one fixed order.
        Assert.Equal(["First", "Second", "Third"], details.Skip(3).ToArray());
        Assert.Equal(["Tie A", "Tie B", "Tie C"], details.Take(3).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(details, surfaces.LiveScreen);
        Assert.Equal(details, surfaces.ListLineItems);
        Assert.Equal(details[0], surfaces.ListFirstProductName);
        Assert.Equal(details, surfaces.Receipt);
        Assert.Equal(details, surfaces.Reprint);
        Assert.Equal(details, (await ReadSurfacesAsync(orderId)).Details);
    }

    private async Task AssertEverySurfaceShowsAsync(Guid orderId, string[] items, string[] firstItemOptions)
    {
        var surfaces = await ReadSurfacesAsync(orderId);

        Assert.Equal(items, surfaces.Details);
        Assert.Equal(firstItemOptions, surfaces.DetailsFirstItemOptions);
        Assert.Equal(items, surfaces.LiveScreen);
        Assert.Equal(items, surfaces.ListLineItems);
        Assert.Equal(items[0], surfaces.ListFirstProductName);
        Assert.Equal(items, surfaces.Receipt);
        Assert.Equal(firstItemOptions, surfaces.ReceiptFirstItemOptions);
        Assert.Equal(items, surfaces.Reprint);
        Assert.Equal(firstItemOptions, surfaces.ReprintFirstItemOptions);
    }

    private async Task<Surfaces> ReadSurfacesAsync(Guid orderId)
    {
        var reads = new OrderReadService(_databases, TimeProvider.System, NullLogger<OrderReadService>.Instance);

        var details = await reads.GetByIdAsync(_tenantId, orderId, Ct);
        Assert.NotNull(details);

        var snapshot = await reads.GetLiveScreenSnapshotAsync(_tenantId, Ct);
        var live = Assert.Single(snapshot.Orders, o => o.Id == orderId);

        var list = await reads.GetListAsync(_tenantId, null, null, null, null, null, null, 1, 50, null, Ct, includeLineItems: true);
        var row = Assert.Single(list.Items, o => o.Id == orderId);

        var receipts = new ReceiptPrintJobService(_databases, DefaultTemplates.Instance, NullTenantChangeNotifier.Instance, NullLogger<ReceiptPrintJobService>.Instance);
        await ClearReceiptJobsAsync(orderId);
        Assert.True(await receipts.TryCreateReceiptJobAsync(_tenantId, orderId, 1, "Test Restaurant", Ct));
        var (receipt, receiptOptions, firstJobId) = await LatestReceiptAsync(orderId);

        await MarkPrintedAsync(firstJobId);
        var history = new PrintJobHistoryService(_databases, DefaultTemplates.Instance, NullTenantChangeNotifier.Instance, NullLogger<PrintJobHistoryService>.Instance);
        var reprint = await history.CreateReprintAsync(_tenantId, firstJobId, "Test Restaurant", Ct);
        Assert.True(reprint.Success, reprint.MessageKey);
        var (reprinted, reprintedOptions, _) = await LatestReceiptAsync(orderId);

        return new Surfaces(
            details.Items.Select(i => i.ProductName).ToArray(),
            details.Items[0].Options.Select(o => o.Name).ToArray(),
            live.Items.Select(i => i.ProductName).ToArray(),
            row.LineItems.Select(i => i.ProductName).ToArray(),
            row.FirstProductName,
            receipt,
            receiptOptions,
            reprinted,
            reprintedOptions);
    }

    private async Task<(string[] Items, string[] FirstItemOptions, Guid JobId)> LatestReceiptAsync(Guid orderId)
    {
        await using var db = await _databases.CreateAsync(_tenantId, Ct);
        var job = (await db.PrintJobs.AsNoTracking().Where(j => j.OrderId == orderId).ToListAsync(Ct))
            .OrderByDescending(j => j.StatusVersion)
            .First();
        using var payload = JsonDocument.Parse(job.PayloadJson);
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var options = items[0].TryGetProperty("options", out var list)
            ? list.EnumerateArray().Select(o => o.GetProperty("name").GetString()!).ToArray()
            : [];
        return (items.Select(i => i.GetProperty("productName").GetString()!).ToArray(), options, job.Id);
    }

    private async Task ClearReceiptJobsAsync(Guid orderId)
    {
        await using var db = await _databases.CreateAsync(_tenantId, Ct);
        await db.PrintJobs.Where(j => j.OrderId == orderId).ExecuteDeleteAsync(Ct);
    }

    private async Task MarkPrintedAsync(Guid jobId)
    {
        await using var db = await _databases.CreateAsync(_tenantId, Ct);
        var job = await db.PrintJobs.SingleAsync(j => j.Id == jobId, Ct);
        job.Status = PrintJobStatus.Printed;
        job.PrintedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> OrderIdAsync(string externalOrderId)
    {
        await using var db = await _databases.CreateAsync(_tenantId, Ct);
        return await db.Orders.Where(o => o.ExternalOrderId == externalOrderId).Select(o => o.Id).SingleAsync(Ct);
    }

    private async Task<OrderSyncService> CreateSyncAsync()
    {
        await using (var db = await _databases.CreateAsync(_tenantId, Ct))
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Platform = FoodPlatform.TrendyolYemek,
                StoreId = "store-1",
                EncryptedApiKey = "fake",
                EncryptedApiSecret = "fake",
                IsActive = true,
                SyncIntervalSeconds = 0
            });
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = TenantOperationalModes.SettingsId,
                OperationalMode = TenantOperationalMode.Live,
                OrderSyncEnabled = true
            });
            await db.SaveChangesAsync(Ct);
        }

        return new OrderSyncService(
            _databases,
            [_client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            NullTenantChangeNotifier.Instance,
            NullLogger<OrderSyncService>.Instance);
    }

    private async Task<OrderSyncCustomerResult> SyncAsync(OrderSyncService sync, ExternalOrderDto order)
    {
        _client.Set(order);
        var result = await sync.SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(0, result.FailedConnections);
        return result;
    }

    /// <summary>The provider's order with <paramref name="items"/> in that order; the options belong to <paramref name="optionsOn"/> (the first line by default).</summary>
    private static ExternalOrderDto Sample(string externalOrderId, string[] items, string? rawPayloadJson = null, string? optionsOn = null) =>
        new(
            FoodPlatform.TrendyolYemek,
            externalOrderId,
            "TY-" + externalOrderId,
            OrderedAt,
            "Ayşe Yılmaz",
            "5313241245",
            "İstiklal Cad. No:1",
            100m,
            0m,
            0m,
            100m,
            PaymentMethod.OnlinePayment,
            PaymentStatus.Paid,
            "Created",
            rawPayloadJson ?? $$"""{"id":"{{externalOrderId}}"}""",
            items
                .Select((name, index) => new ExternalOrderItemDto(
                    $"{externalOrderId}-line-{index + 1}",
                    name,
                    1,
                    10m,
                    10m,
                    null,
                    name == (optionsOn ?? items[0]) ? ProviderOptions.Select(o => new ExternalOrderItemOptionDto(o, 1m)).ToArray() : []))
                .ToArray());

    private static Order NewOrder(Guid id, string externalOrderId) => new()
    {
        Id = id,
        Platform = FoodPlatform.TrendyolYemek,
        ExternalOrderId = externalOrderId,
        ExternalOrderCode = "TY-" + externalOrderId,
        IdempotencyKey = "TrendyolYemek:" + externalOrderId,
        InternalStatus = OrderStatus.New,
        PlatformStatus = "Created",
        CustomerName = "Ayşe Yılmaz",
        TotalAmount = 100m,
        CreatedAtPlatform = OrderedAt,
        ReceivedAt = DateTime.UtcNow,
        RawPayloadJson = "{}"
    };

    private static OrderItem LegacyItem(string name, DateTime storedAt, string[] options) => new()
    {
        ProductName = name,
        Quantity = 1,
        UnitPrice = 10m,
        TotalPrice = 10m,
        CreatedAt = storedAt,
        UpdatedAt = storedAt,
        Options = options.Select(o => new OrderItemOption { Name = o, Price = 1m, CreatedAt = storedAt, UpdatedAt = storedAt }).ToList()
    };

    private sealed record Surfaces(
        string[] Details,
        string[] DetailsFirstItemOptions,
        string[] LiveScreen,
        string[] ListLineItems,
        string? ListFirstProductName,
        string[] Receipt,
        string[] ReceiptFirstItemOptions,
        string[] Reprint,
        string[] ReprintFirstItemOptions);

    private sealed class FakeClient : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(_orders);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DefaultTemplates : IReceiptTemplateSettingsService
    {
        public static readonly DefaultTemplates Instance = new();

        public Task<ReceiptTemplateSettings> GetAsync(Guid customerId, string? customerDisplayName, string? defaultReceiptLanguage, CancellationToken ct) =>
            Task.FromResult(ReceiptTemplateSettings.CreateDefaults(customerDisplayName, defaultReceiptLanguage));

        public Task<ReceiptTemplateSettings> UpdateAsync(Guid customerId, string? customerDisplayName, UpdateReceiptTemplateSettingsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>The real tenant model (its SQL Server-only column types swapped for SQLite), one in-memory database per tenant.</summary>
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
                using var setup = new SqliteSchemaTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult(new TenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }
}
