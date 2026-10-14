using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Realtime;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-44: an order's items and their options keep the order of the provider's list on every surface that shows them
/// (order details, the Live Screen snapshot, the list's line items, a receipt and its reprint). A provider list that
/// only changes the order of the same lines, or of one line's options, is an update like any other: the new positions
/// are stored, the order's version moves on once and one change notification is sent. Rows stored before positions
/// were kept fall back to their stored time, then their Id, and every surface still agrees. Runs the real sync, read
/// and receipt services over the real tenant model on SQLite; it does not prove SQL Server translation.
/// </summary>
public sealed class OrderItemSequenceTests : IDisposable
{
    private static readonly DateTime OrderedAt = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    // Provider lines with stable identities: a line keeps its id wherever the provider lists it.
    private static readonly Line Kebap = new("ty-line-2001", "Adana Kebap", ["Acılı", "Ekstra lavaş", "Az pişmiş", "Bol soğan", "Yoğurtlu"]);
    private static readonly Line Ayran = new("ty-line-2002", "Ayran", []);
    private static readonly Line Kunefe = new("ty-line-2003", "Künefe", ["Kaymaklı"]);
    private static readonly Line Su = new("ty-line-2004", "Su", []);

    // Neither alphabetical nor any other order a database or GUID sort would produce by chance (8! orders).
    private static readonly Line[] Menu =
    [
        new("ty-line-1001", "Mercimek Çorbası", ["Limonlu", "Ekstra ekmek", "Az tuzlu"]),
        Kebap,
        Ayran,
        Kunefe,
        new("ty-line-1005", "Lahmacun", []),
        new("ty-line-1006", "Çoban Salata", []),
        new("ty-line-1007", "Baklava", []),
        Su
    ];

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TenantSqlite _databases = new();
    private readonly FakeClient _client = new();
    private readonly RecordingTenantChangeNotifier _notifier = new();

    public void Dispose() => _databases.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewOrder_KeepsTheProviderSequence_OnEverySurface()
    {
        var sync = await CreateSyncAsync();
        await SyncAsync(sync, Sample("ext-1", Menu));

        await AssertEverySurfaceShowsAsync(await OrderIdAsync("ext-1"), Menu);
    }

    [Fact]
    public async Task ProviderUpdate_StoresTheNewSequence()
    {
        var sync = await CreateSyncAsync();
        await SyncAsync(sync, Sample("ext-2", [Ayran, Kebap, Kunefe]));

        // A content change (a new line) rewrites the items in the provider's new order.
        Line[] updated = [Kunefe, Su, Kebap, Ayran];
        var result = await SyncAsync(sync, Sample("ext-2", updated, rawPayloadJson: """{"id":"ext-2","v":2}"""));
        Assert.Equal(1, result.UpdatedCount);

        await AssertEverySurfaceShowsAsync(await OrderIdAsync("ext-2"), updated);
    }

    [Fact]
    public async Task ProviderReorderOfTheSameLines_IsStoredAndAnnouncedOnce_AndShownEverywhere()
    {
        var sync = await CreateSyncAsync();
        await SyncAsync(sync, Sample("ext-3", [Ayran, Kebap, Kunefe]));
        var orderId = await OrderIdAsync("ext-3");
        var before = await StoredAsync(orderId);
        var mark = _notifier.Sent.Count;

        // The same lines with the same ids, options and payload; only their order differs.
        Line[] reordered = [Kunefe, Ayran, Kebap];
        var result = await SyncAsync(sync, Sample("ext-3", reordered));

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
        await AssertStoredAndAnnouncedOnceAsync(orderId, before, mark, reordered);
        await AssertEverySurfaceShowsAsync(orderId, reordered);

        // The stored order now matches the provider's: the next identical poll writes and announces nothing.
        var again = await SyncAsync(sync, Sample("ext-3", reordered));
        Assert.Equal(1, again.UnchangedCount);
        Assert.Single(OrderChangesSince(mark, orderId));
    }

    [Fact]
    public async Task ProviderReorderOfOneLinesOptions_IsStoredAndAnnouncedOnce_AndShownEverywhere()
    {
        var sync = await CreateSyncAsync();
        await SyncAsync(sync, Sample("ext-4", [Kebap, Ayran]));
        var orderId = await OrderIdAsync("ext-4");
        var before = await StoredAsync(orderId);
        var mark = _notifier.Sent.Count;

        // The same lines in the same order; only the kebab's options come in a different order.
        Line[] reordered = [Kebap with { Options = ["Yoğurtlu", "Acılı", "Az pişmiş", "Ekstra lavaş", "Bol soğan"] }, Ayran];
        var result = await SyncAsync(sync, Sample("ext-4", reordered));

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
        await AssertStoredAndAnnouncedOnceAsync(orderId, before, mark, reordered);
        await AssertEverySurfaceShowsAsync(orderId, reordered);

        var again = await SyncAsync(sync, Sample("ext-4", reordered));
        Assert.Equal(1, again.UnchangedCount);
        Assert.Single(OrderChangesSince(mark, orderId));
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

        var surfaces = await ReadSurfacesAsync(orderId, "Tie A");
        var details = surfaces.Details;

        // Stored time first; the three that share it come before the later ones, in one fixed order.
        Assert.Equal(["First", "Second", "Third"], details.Skip(3).ToArray());
        Assert.Equal(["Tie A", "Tie B", "Tie C"], details.Take(3).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(details, surfaces.LiveScreen);
        Assert.Equal(details, surfaces.ListLineItems);
        Assert.Equal(details[0], surfaces.ListFirstProductName);
        Assert.Equal(details, surfaces.Receipt);
        Assert.Equal(details, surfaces.Reprint);
        Assert.Equal(details, (await ReadSurfacesAsync(orderId, "Tie A")).Details);
    }

    /// <summary>
    /// The update stored every position (0..n-1, options too), moved the order's version on by exactly one and sent
    /// exactly one change notification for the order, carrying that version.
    /// </summary>
    private async Task AssertStoredAndAnnouncedOnceAsync(Guid orderId, Stored before, int mark, Line[] lines)
    {
        var after = await StoredAsync(orderId);

        Assert.Equal(lines.Select(l => l.Name).ToArray(), after.Items.Select(i => i.Name).ToArray());
        Assert.Equal(Enumerable.Range(0, lines.Length).ToArray(), after.Items.Select(i => i.Sequence).ToArray());
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal(lines[i].Options, after.Items[i].Options.Select(o => o.Name).ToArray());
            Assert.Equal(Enumerable.Range(0, lines[i].Options.Length).ToArray(), after.Items[i].Options.Select(o => o.Sequence).ToArray());
        }

        Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
        var change = Assert.Single(OrderChangesSince(mark, orderId));
        Assert.Equal(after.ChangeVersion, change.Version);
    }

    private List<OrderChanged> OrderChangesSince(int mark, Guid orderId) =>
        _notifier.Sent
            .Skip(mark)
            .Where(s => s.TenantId == _tenantId)
            .Select(s => s.Change)
            .OfType<OrderChanged>()
            .Where(c => c.OrderId == orderId)
            .ToList();

    private async Task<Stored> StoredAsync(Guid orderId)
    {
        await using var db = await _databases.CreateAsync(_tenantId, Ct);
        var version = await db.Orders.Where(o => o.Id == orderId).Select(o => o.ChangeVersion).SingleAsync(Ct);
        var items = (await db.OrderItems.AsNoTracking().Include(i => i.Options).Where(i => i.OrderId == orderId).ToListAsync(Ct))
            .OrderBy(i => i.Sequence)
            .Select(i => new StoredItem(
                i.ProductName,
                i.Sequence,
                i.Options.OrderBy(o => o.Sequence).Select(o => new StoredOption(o.Name, o.Sequence)).ToArray()))
            .ToArray();
        return new Stored(version, items);
    }

    private async Task AssertEverySurfaceShowsAsync(Guid orderId, Line[] lines)
    {
        string[] items = lines.Select(l => l.Name).ToArray();
        // The options of the first line that has some; every surface shows them in the provider's order too.
        var withOptions = lines.First(l => l.Options.Length > 0);
        var surfaces = await ReadSurfacesAsync(orderId, withOptions.Name);

        Assert.Equal(items, surfaces.Details);
        Assert.Equal(withOptions.Options, surfaces.DetailsOptions);
        Assert.Equal(items, surfaces.LiveScreen);
        Assert.Equal(items, surfaces.ListLineItems);
        Assert.Equal(items[0], surfaces.ListFirstProductName);
        Assert.Equal(items, surfaces.Receipt);
        Assert.Equal(withOptions.Options, surfaces.ReceiptOptions);
        Assert.Equal(items, surfaces.Reprint);
        Assert.Equal(withOptions.Options, surfaces.ReprintOptions);
    }

    private async Task<Surfaces> ReadSurfacesAsync(Guid orderId, string optionsOf)
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
        var (receipt, receiptOptions, firstJobId) = await LatestReceiptAsync(orderId, optionsOf);

        await MarkPrintedAsync(firstJobId);
        var history = new PrintJobHistoryService(_databases, DefaultTemplates.Instance, NullTenantChangeNotifier.Instance, NullLogger<PrintJobHistoryService>.Instance);
        var reprint = await history.CreateReprintAsync(_tenantId, firstJobId, "Test Restaurant", Ct);
        Assert.True(reprint.Success, reprint.MessageKey);
        var (reprinted, reprintedOptions, _) = await LatestReceiptAsync(orderId, optionsOf);

        return new Surfaces(
            details.Items.Select(i => i.ProductName).ToArray(),
            details.Items.Single(i => i.ProductName == optionsOf).Options.Select(o => o.Name).ToArray(),
            live.Items.Select(i => i.ProductName).ToArray(),
            row.LineItems.Select(i => i.ProductName).ToArray(),
            row.FirstProductName,
            receipt,
            receiptOptions,
            reprinted,
            reprintedOptions);
    }

    private async Task<(string[] Items, string[] Options, Guid JobId)> LatestReceiptAsync(Guid orderId, string optionsOf)
    {
        await using var db = await _databases.CreateAsync(_tenantId, Ct);
        var job = (await db.PrintJobs.AsNoTracking().Where(j => j.OrderId == orderId).ToListAsync(Ct))
            .OrderByDescending(j => j.StatusVersion)
            .First();
        using var payload = JsonDocument.Parse(job.PayloadJson);
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var item = items.Single(i => i.GetProperty("productName").GetString() == optionsOf);
        var options = item.TryGetProperty("options", out var list)
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
            _notifier,
            NullLogger<OrderSyncService>.Instance);
    }

    private async Task<OrderSyncCustomerResult> SyncAsync(OrderSyncService sync, ExternalOrderDto order)
    {
        _client.Set(order);
        var result = await sync.SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(0, result.FailedConnections);
        return result;
    }

    /// <summary>The provider's order with <paramref name="lines"/> in that order, each under its own stable id.</summary>
    private static ExternalOrderDto Sample(string externalOrderId, Line[] lines, string? rawPayloadJson = null) =>
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
            lines
                .Select(line => new ExternalOrderItemDto(
                    line.Id,
                    line.Name,
                    1,
                    10m,
                    10m,
                    null,
                    line.Options.Select(o => new ExternalOrderItemOptionDto(o, 1m)).ToArray()))
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

    /// <summary>One provider line: its stable id, its name and its options in the provider's order.</summary>
    private sealed record Line(string Id, string Name, string[] Options);

    private sealed record Stored(long ChangeVersion, StoredItem[] Items);

    private sealed record StoredItem(string Name, int Sequence, StoredOption[] Options);

    private sealed record StoredOption(string Name, int Sequence);

    private sealed record Surfaces(
        string[] Details,
        string[] DetailsOptions,
        string[] LiveScreen,
        string[] ListLineItems,
        string? ListFirstProductName,
        string[] Receipt,
        string[] ReceiptOptions,
        string[] Reprint,
        string[] ReprintOptions);

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
