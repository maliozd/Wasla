using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Printing;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-72: when the save of an order fails, the sync failure report (<see cref="SyncLog"/>,
/// <see cref="IntegrationError"/>, failure counters) must not persist the order changes that failed. The failure is
/// injected into the order's own save while its graph is tracked (an insert of its items), so the attempt really
/// leaves Added and Modified entities behind; every assertion reads the database through a new context. Tenant
/// databases are SQLite files with the production tenant model, and the automation services are the real ones (the
/// provider approval is recorded instead of called). This proves Wasla's persistence logic, not SQL Server locking.
/// </summary>
public sealed class OrderSyncFailureIsolationTests : IDisposable
{
    private const string FailOrderItemInsert = "INSERT INTO \"OrderItems\"";

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly RecordingTenantFactory _factory;
    private readonly OperationalModeTestClock _clock = new();
    private readonly SqliteConnection _centralConnection = new("Data Source=:memory:");
    private readonly RecordingApprovals _approvals;
    private readonly CountingReceipts _receipts;
    private readonly FakeProvider _trendyol = new(FoodPlatform.TrendyolYemek);
    private readonly FakeProvider _yemeksepeti = new(FoodPlatform.Yemeksepeti);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();

    public OrderSyncFailureIsolationTests()
    {
        _factory = new RecordingTenantFactory(_tenants);
        _approvals = new RecordingApprovals(_tenants);
        _receipts = new CountingReceipts(new OrderReceiptCreationService(
            _tenants, Central(), new ReceiptPrintJobService(_tenants, new DefaultTemplates(), NullLogger<ReceiptPrintJobService>.Instance),
            NullLogger<OrderReceiptCreationService>.Instance));

        _centralConnection.Open();
        using var central = Central();
        central.Database.EnsureCreated();
        foreach (var id in new[] { _tenant, _otherTenant })
        {
            central.Tenants.Add(new Tenant
            {
                Id = id,
                Name = "Restaurant " + id.ToString("N")[..6],
                Slug = "r-" + id.ToString("N")[..12],
                PrimaryDomain = "r-" + id.ToString("N")[..12] + ".wasla.local",
                DatabaseName = "unused",
                EncryptedConnectionString = "unused",
                EncryptionKeyVersion = 1,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        central.SaveChanges();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InsertFailure_StoresTheErrorRecords_ButNotTheOrderOrItsItems()
    {
        await SeedTenantAsync(_tenant, autoApprove: true, autoPrint: true);
        _trendyol.Orders = [NewOrder("insert-1")];
        _tenants.FailNextStatementContaining = FailOrderItemInsert;

        var result = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal(1, result.FailedConnections);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Empty(await db.Orders.ToListAsync(Ct));
        Assert.Empty(await db.OrderItems.ToListAsync(Ct));
        Assert.Empty(await db.OrderItemOptions.ToListAsync(Ct));
        var log = await db.SyncLogs.SingleAsync(Ct);
        Assert.Equal(SyncStatus.Failed, log.Status);
        Assert.Equal(0, log.OrdersInserted);
        var error = await db.IntegrationErrors.SingleAsync(Ct);
        Assert.Equal(nameof(DbUpdateException), error.ErrorType);
        Assert.Equal(log.ErrorMessage, error.ErrorMessage);
        Assert.Equal(1, (await db.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
        AssertNoCustomerDataOrPayload(error.ErrorMessage);
        Assert.Empty(_approvals.Approved);
        Assert.Empty(_receipts.Calls);
        Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task RetryAfterInsertFailure_StoresOneOrderGraph_AndRunsNewOrderSideEffectsOnce()
    {
        await SeedTenantAsync(_tenant, autoApprove: true, autoPrint: true);
        _trendyol.Orders = [NewOrder("insert-2")];
        _tenants.FailNextStatementContaining = FailOrderItemInsert;
        await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        var retry = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);
        var duplicate = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal((0, 1), (retry.FailedConnections, retry.InsertedCount));
        Assert.Equal((0, 0, 1), (duplicate.InsertedCount, duplicate.UpdatedCount, duplicate.UnchangedCount));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var order = await db.Orders.SingleAsync(Ct);
        Assert.Equal("insert-2", order.ExternalOrderId);
        Assert.Equal(OrderStatus.Accepted, order.InternalStatus);
        Assert.Equal(ItemsOf(NewOrder("insert-2")), await StoredItemsAsync(db, order.Id));
        Assert.Equal([order.Id], _approvals.Approved);
        Assert.Equal([order.Id], _receipts.Calls);
        Assert.Equal(order.Id, (await db.PrintJobs.SingleAsync(Ct)).OrderId);
        Assert.Equal(0, (await db.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
        Assert.Equal(
            [SyncStatus.Failed, SyncStatus.Success, SyncStatus.Success],
            (await db.SyncLogs.OrderBy(l => l.StartedAt).ToListAsync(Ct)).Select(l => l.Status));
    }

    [Fact]
    public async Task UpdateFailure_LeavesTheStoredOrderAndItemsAsTheyWere()
    {
        await SeedTenantAsync(_tenant, autoApprove: false, autoPrint: true);
        _trendyol.Orders = [NewOrder("update-1")];
        await Sync().SyncCustomerWithResultAsync(_tenant, Ct);
        var before = await SnapshotAsync("update-1");

        _trendyol.Orders = [ChangedOrder("update-1")];
        _tenants.FailNextStatementContaining = FailOrderItemInsert;
        var result = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal((1, 0), (result.FailedConnections, result.UpdatedCount));
        Assert.Equal(before, await SnapshotAsync("update-1"));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(SyncStatus.Failed, (await db.SyncLogs.OrderBy(l => l.StartedAt).ToListAsync(Ct)).Last().Status);
        Assert.Equal(nameof(DbUpdateException), (await db.IntegrationErrors.SingleAsync(Ct)).ErrorType);
        Assert.Empty(_receipts.Calls);
        Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task RetryAfterUpdateFailure_AppliesTheUpdateOnce_WithoutDuplicateItems()
    {
        await SeedTenantAsync(_tenant, autoApprove: false, autoPrint: true);
        _trendyol.Orders = [NewOrder("update-2")];
        await Sync().SyncCustomerWithResultAsync(_tenant, Ct);
        _trendyol.Orders = [ChangedOrder("update-2")];
        _tenants.FailNextStatementContaining = FailOrderItemInsert;
        await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        var retry = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);
        var duplicate = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal((0, 1), (retry.FailedConnections, retry.UpdatedCount));
        Assert.Equal((0, 1), (duplicate.UpdatedCount, duplicate.UnchangedCount));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var order = await db.Orders.SingleAsync(Ct);
        var changed = ChangedOrder("update-2");
        Assert.Equal(OrderStatus.Accepted, order.InternalStatus);
        Assert.Equal(changed.ExternalStatus, order.PlatformStatus);
        Assert.Equal(changed.Total, order.TotalAmount);
        Assert.Equal(changed.CustomerNote, order.CustomerNote);
        Assert.Equal(ItemsOf(changed), await StoredItemsAsync(db, order.Id));
        // The provider-accepted receipt follows the status change exactly once.
        Assert.Equal([order.Id], _receipts.Calls);
        Assert.Equal(order.Id, (await db.PrintJobs.SingleAsync(Ct)).OrderId);
        Assert.Empty(_approvals.Approved);
    }

    [Fact]
    public async Task FailedOrderOfOneConnection_IsNotSavedLaterInTheSamePass()
    {
        await SeedTenantAsync(_tenant, autoApprove: true, autoPrint: true);
        await AddConnectionAsync(_tenant, FoodPlatform.Yemeksepeti);
        _trendyol.Orders = [NewOrder("pass-trendyol")];
        _yemeksepeti.Orders = [NewOrder("pass-yemeksepeti", FoodPlatform.Yemeksepeti, "CREATED")];
        // Whichever connection runs first fails on its order; the other connection then saves its own work.
        _tenants.FailNextStatementContaining = FailOrderItemInsert;

        var result = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal((2, 1, 1), (result.ConnectionCount, result.FailedConnections, result.InsertedCount));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var connections = await db.PlatformConnections.ToDictionaryAsync(c => c.Id, Ct);
        var failedPlatform = connections[(await db.IntegrationErrors.SingleAsync(Ct)).PlatformConnectionId!.Value].Platform;
        var stored = await db.Orders.SingleAsync(Ct);
        Assert.NotEqual(failedPlatform, stored.Platform);
        Assert.Equal(ItemsOf(NewOrder(stored.ExternalOrderId)), await StoredItemsAsync(db, stored.Id));
        Assert.Equal(2, await db.OrderItems.CountAsync(Ct));
        Assert.Equal([stored.Id], _approvals.Approved);
        Assert.Equal(1, connections.Values.Single(c => c.Platform == failedPlatform).ConsecutiveFailures);
        Assert.Equal(0, connections.Values.Single(c => c.Platform != failedPlatform).ConsecutiveFailures);
    }

    [Fact]
    public async Task FailedCheckpointSave_DoesNotAdvanceTheCheckpoint()
    {
        var checkpoint = _clock.UtcNow.AddMinutes(-20);
        await SeedTenantAsync(_tenant, autoApprove: true, autoPrint: false, checkpoint);
        _trendyol.Orders = [NewOrder("checkpoint-1")];
        _tenants.FailNextStatementContaining = "\"LastSuccessfulSync\" = ";

        var failed = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal(1, failed.FailedConnections);
        Assert.Equal(checkpoint, await CheckpointAsync());
        // The order was saved (and its side effects ran) before the checkpoint; the next run must not repeat them.
        var order = await OrderAsync("checkpoint-1");
        Assert.Equal([order.Id], _approvals.Approved);

        var retry = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal((0, 0), (retry.FailedConnections, retry.InsertedCount));
        Assert.Equal(_clock.UtcNow, await CheckpointAsync());
        Assert.Equal([order.Id], _approvals.Approved);
    }

    [Fact]
    public async Task HostCancellationWhileAnOrderIsTracked_RecordsNoFailure_AndTheRetryInsertsItOnce()
    {
        await SeedTenantAsync(_tenant, autoApprove: true, autoPrint: true);
        _trendyol.Orders = [NewOrder("cancel-1")];
        using var host = new CancellationTokenSource();
        _tenants.FailWith = () =>
        {
            host.Cancel();
            return new OperationCanceledException(host.Token);
        };
        _tenants.FailNextStatementContaining = FailOrderItemInsert;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync().SyncCustomerWithResultAsync(_tenant, host.Token));

        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
        {
            Assert.Empty(await db.Orders.ToListAsync(Ct));
            Assert.Empty(await db.OrderItems.ToListAsync(Ct));
            Assert.Empty(await db.SyncLogs.ToListAsync(Ct));
            Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
            Assert.Equal(0, (await db.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
        }

        await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        var order = await OrderAsync("cancel-1");
        Assert.Equal([order.Id], _approvals.Approved);
        Assert.Equal([order.Id], _receipts.Calls);
    }

    [Fact]
    public async Task FailureReportThatFails_DoesNotSaveTheFailedOrder()
    {
        await SeedTenantAsync(_tenant, autoApprove: true, autoPrint: true);
        _trendyol.Orders = [NewOrder("report-1")];
        var failures = 0;
        _tenants.FailWith = () =>
        {
            // The order save fails first; then the failure report itself fails.
            if (++failures == 1)
                _tenants.FailNextStatementContaining = "INSERT INTO \"SyncLogs\"";
            return new InvalidOperationException("simulated tenant database failure");
        };
        _tenants.FailNextStatementContaining = FailOrderItemInsert;

        await Assert.ThrowsAsync<DbUpdateException>(() => Sync().SyncCustomerWithResultAsync(_tenant, Ct));

        Assert.Equal(2, failures);
        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
        {
            Assert.Empty(await db.Orders.ToListAsync(Ct));
            Assert.Empty(await db.OrderItems.ToListAsync(Ct));
            Assert.Empty(await db.SyncLogs.ToListAsync(Ct));
            Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
        }

        await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        var order = await OrderAsync("report-1");
        Assert.Equal([order.Id], _approvals.Approved);
        Assert.Equal([order.Id], _receipts.Calls);
    }

    [Fact]
    public async Task FailureReport_IsWrittenOnlyToTheFailingTenantsDatabase()
    {
        await SeedTenantAsync(_tenant, autoApprove: false, autoPrint: false);
        await SeedTenantAsync(_otherTenant, autoApprove: false, autoPrint: false);
        _trendyol.Orders = [NewOrder("tenant-order")];
        _tenants.FailNextStatementContaining = FailOrderItemInsert;

        var failed = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal(1, failed.FailedConnections);
        Assert.NotEmpty(_factory.Requested);
        Assert.All(_factory.Requested, id => Assert.Equal(_tenant, id));
        await using (var other = await _tenants.CreateAsync(_otherTenant, Ct))
        {
            Assert.Empty(await other.SyncLogs.ToListAsync(Ct));
            Assert.Empty(await other.IntegrationErrors.ToListAsync(Ct));
            Assert.Empty(await other.Orders.ToListAsync(Ct));
            Assert.Equal(0, (await other.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
        }

        _factory.Requested.Clear();
        var succeeded = await Sync().SyncCustomerWithResultAsync(_otherTenant, Ct);

        Assert.Equal((0, 1), (succeeded.FailedConnections, succeeded.InsertedCount));
        Assert.All(_factory.Requested, id => Assert.Equal(_otherTenant, id));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Empty(await db.Orders.ToListAsync(Ct));
        Assert.Single(await db.IntegrationErrors.ToListAsync(Ct));
        await using var otherAfter = await _tenants.CreateAsync(_otherTenant, Ct);
        Assert.Empty(await otherAfter.IntegrationErrors.ToListAsync(Ct));
        Assert.Equal(SyncStatus.Success, (await otherAfter.SyncLogs.SingleAsync(Ct)).Status);
        Assert.Equal("tenant-order", (await otherAfter.Orders.SingleAsync(Ct)).ExternalOrderId);
    }

    public void Dispose()
    {
        _tenants.Dispose();
        _centralConnection.Dispose();
    }

    private OrderSyncService Sync() =>
        new(
            _factory,
            new IFoodPlatformClient[] { _trendyol, _yemeksepeti },
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new OrderAutoApproveService(_tenants, _approvals, _receipts, NullLogger<OrderAutoApproveService>.Instance),
            _receipts,
            NullLogger<OrderSyncService>.Instance,
            businessSubtypeReader: null,
            time: _clock);

    private CentralDbContext Central() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_centralConnection).Options);

    private async Task SeedTenantAsync(Guid tenant, bool autoApprove, bool autoPrint, DateTime? checkpoint = null)
    {
        await _tenants.SeedSettingsAsync(tenant, TenantOperationalMode.Live, autoApprove, autoPrint);
        await AddConnectionAsync(tenant, FoodPlatform.TrendyolYemek, checkpoint);
    }

    private async Task AddConnectionAsync(Guid tenant, FoodPlatform platform, DateTime? checkpoint = null)
    {
        await using var db = await _tenants.CreateAsync(tenant, Ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = platform,
            StoreId = "store-" + platform,
            EncryptedApiKey = "fake-encrypted-key",
            EncryptedApiSecret = "fake-encrypted-secret",
            IsActive = true,
            SyncIntervalSeconds = 0,
            LastSuccessfulSync = checkpoint
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<DateTime?> CheckpointAsync()
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var stored = await db.PlatformConnections.Select(c => c.LastSuccessfulSync).SingleAsync(Ct);
        return stored is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }

    private async Task<Order> OrderAsync(string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
    }

    private async Task<OrderSnapshot> SnapshotAsync(string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
        return new OrderSnapshot(
            order.Id,
            order.InternalStatus,
            order.PlatformStatus,
            order.TotalAmount,
            order.CustomerName,
            order.CustomerNote,
            order.RawPayloadJson,
            order.AcceptedAt,
            order.UpdatedAt,
            string.Join(" / ", await StoredItemsAsync(db, order.Id)));
    }

    /// <summary>Every stored item of the order with its options, in a stable order (stored order is not kept).</summary>
    private static async Task<List<string>> StoredItemsAsync(TenantDbContext db, Guid orderId)
    {
        var items = await db.OrderItems.AsNoTracking().Include(i => i.Options).Where(i => i.OrderId == orderId).ToListAsync(Ct);
        return items
            .Select(i => Describe(i.ProductName, i.Quantity, i.UnitPrice, i.TotalPrice, i.Notes, i.Options.Select(o => (o.Name, o.Price))))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ItemsOf(ExternalOrderDto order) =>
        order.Items
            .Select(i => Describe(i.ProductName, i.Quantity, i.UnitPrice, i.TotalPrice, i.Notes, i.Options.Select(o => (o.Name, o.Price))))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Describe(string name, int quantity, decimal unit, decimal total, string? notes, IEnumerable<(string Name, decimal Price)> options) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{name} x{quantity} {unit:0.00}/{total:0.00} [{notes}] {{{string.Join(",", options.Select(o => $"{o.Name}:{o.Price:0.00}").Order(StringComparer.Ordinal))}}}");

    private static void AssertNoCustomerDataOrPayload(string message)
    {
        foreach (var value in new[] { "Gizlisoy", "5321112233", "Saklı Sokak", "Zile basmayın", "\"lines\"", "fake-encrypted" })
            Assert.DoesNotContain(value, message, StringComparison.Ordinal);
    }

    /// <summary>A new provider order: two items, one with options.</summary>
    private static ExternalOrderDto NewOrder(string externalOrderId, FoodPlatform platform = FoodPlatform.TrendyolYemek, string status = "Created") =>
        new(
            Platform: platform,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: "N-" + externalOrderId,
            OrderedAtUtc: new DateTime(2026, 10, 2, 8, 55, 0, DateTimeKind.Utc),
            CustomerName: "Ayşe Gizlisoy",
            CustomerPhone: "5321112233",
            CustomerAddress: "Saklı Sokak 7",
            Subtotal: 150m,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: 150m,
            PaymentMethod: PaymentMethod.CreditCard,
            PaymentStatus: PaymentStatus.Paid,
            ExternalStatus: status,
            RawPayloadJson: """{"lines":[],"customerNote":"Zile basmayın"}""",
            Items:
            [
                new ExternalOrderItemDto(externalOrderId + "-1", "Lahmacun", 1, 100m, 100m, "Acılı",
                    [new ExternalOrderItemOptionDto("Extra Limon", 0m), new ExternalOrderItemOptionDto("Ayran", 20m)]),
                new ExternalOrderItemDto(externalOrderId + "-2", "Künefe", 1, 50m, 50m, null, [])
            ],
            CustomerNote: "Zile basmayın");

    /// <summary>
    /// The same order as accepted at the provider, with a new total and note: the first item changes quantity and
    /// options, the second item is removed and a third is added.
    /// </summary>
    private static ExternalOrderDto ChangedOrder(string externalOrderId) =>
        NewOrder(externalOrderId) with
        {
            ExternalStatus = "Picking",
            Total = 260m,
            CustomerNote = "Kapıda ödeme yok",
            RawPayloadJson = """{"changed":true}""",
            Items =
            [
                new ExternalOrderItemDto(externalOrderId + "-1", "Lahmacun", 2, 100m, 200m, "Acılı",
                    [new ExternalOrderItemOptionDto("Şalgam", 15m)]),
                new ExternalOrderItemDto(externalOrderId + "-3", "Baklava", 1, 60m, 60m, null, [])
            ]
        };

    private sealed record OrderSnapshot(
        Guid Id,
        OrderStatus InternalStatus,
        string? PlatformStatus,
        decimal TotalAmount,
        string? CustomerName,
        string? CustomerNote,
        string? RawPayloadJson,
        DateTime? AcceptedAt,
        DateTime UpdatedAt,
        string Items);

    /// <summary>Records which tenant every context the sync service opens belongs to.</summary>
    private sealed class RecordingTenantFactory(ITenantDbContextFactory inner) : ITenantDbContextFactory
    {
        public List<Guid> Requested { get; } = [];

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            Requested.Add(customerId);
            return inner.CreateAsync(customerId, ct);
        }
    }

    private sealed class FakeProvider(FoodPlatform platform) : IFoodPlatformClient
    {
        public IReadOnlyCollection<ExternalOrderDto> Orders { get; set; } = [];

        public FoodPlatform Platform => platform;

        public TimeSpan? MaxFetchWindow => null;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(Orders);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            throw new InvalidOperationException("Accepting goes through the recorded approvals in these tests.");

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>Records automatic approvals (which would call the provider) and marks the order accepted locally.</summary>
    private sealed class RecordingApprovals(OperationalModeTestDatabases tenants) : IOrderActionService
    {
        public List<Guid> Approved { get; } = [];

        public async Task<OrderActionResult> TryApproveAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            Approved.Add(orderId);
            await using var db = await tenants.CreateAsync(customerId, ct);
            await db.Orders.Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.InternalStatus, OrderStatus.Accepted), ct);
            return new OrderActionResult(true, "Orders.ApproveSuccess");
        }

        public Task<bool> TryUpdateStatusAsync(Guid customerId, Guid orderId, OrderStatus newStatus, CancellationToken ct) => throw new NotSupportedException();
        public Task<OrderActionResult> TryRejectAsync(Guid customerId, Guid orderId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OrderActionResult> MarkPreparingAsync(Guid customerId, Guid orderId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OrderActionResult> MarkReadyForPickupAsync(Guid customerId, Guid orderId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OrderActionResult> MarkOnTheWayAsync(Guid customerId, Guid orderId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OrderActionResult> MarkDeliveredAsync(Guid customerId, Guid orderId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// Counts automatic receipt requests before the real service runs. The PrintJob pipeline refuses a second active
    /// job, so the job count alone would hide a repeated request.
    /// </summary>
    private sealed class CountingReceipts(IOrderReceiptCreationService inner) : IOrderReceiptCreationService
    {
        public List<Guid> Calls { get; } = [];

        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            Calls.Add(orderId);
            return inner.TryCreateOnOrderAcceptedAsync(customerId, orderId, ct);
        }
    }

    private sealed class DefaultTemplates : IReceiptTemplateSettingsService
    {
        public Task<ReceiptTemplateSettings> GetAsync(Guid customerId, string? customerDisplayName, string? defaultReceiptLanguage, CancellationToken ct) =>
            Task.FromResult(ReceiptTemplateSettings.CreateDefaults(customerDisplayName, defaultReceiptLanguage));

        public Task<ReceiptTemplateSettings> UpdateAsync(Guid customerId, string? customerDisplayName, UpdateReceiptTemplateSettingsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
