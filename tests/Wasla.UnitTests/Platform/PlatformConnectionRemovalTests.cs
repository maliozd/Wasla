using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Platform.Dtos;
using Wasla.Application.PlatformConnections;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Removing a platform connection deletes its row and stored credentials and nothing else: imported orders, sync logs and
/// integration errors stay (none of them has a foreign key to the row), the Worker never loads the connection again, and
/// the same platform and store can be connected anew. A repeated or stale removal (an old id after a reconnect, another
/// tenant's id) deletes nothing. A connection removed while the Worker is syncing it ends that connection's pass without
/// a failure record and without stopping the tenant's other connections. Tenant databases are SQLite files with the
/// production tenant model; this proves the persistence logic, not SQL Server locking.
/// </summary>
public sealed class PlatformConnectionRemovalTests : IDisposable
{
    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly FakeProvider _trendyol = new(FoodPlatform.TrendyolYemek);
    private readonly FakeProvider _yemeksepeti = new(FoodPlatform.Yemeksepeti);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> States() => new() { "active", "inactive", "circuit-open" };

    [Theory]
    [MemberData(nameof(States))]
    public async Task Remove_DeletesTheConnectionAndItsCredentials_AndKeepsOrdersAndSyncHistory(string state)
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        var id = await AddConnectionAsync(_tenant, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        _trendyol.Orders = [Order("ty-1")];
        Assert.Equal(1, (await Sync().SyncCustomerWithResultAsync(_tenant, Ct)).InsertedCount);
        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
        {
            db.IntegrationErrors.Add(new IntegrationError
            {
                Platform = FoodPlatform.TrendyolYemek, PlatformConnectionId = id, ErrorType = "Timeout", ErrorMessage = "timed out"
            });
            var connection = await db.PlatformConnections.SingleAsync(c => c.Id == id, Ct);
            connection.IsActive = state != "inactive";
            if (state == "circuit-open")
            {
                connection.ConsecutiveFailures = 5;
                connection.CircuitOpenUntil = DateTime.UtcNow.AddMinutes(5);
            }

            await db.SaveChangesAsync(Ct);
        }

        var order = await SingleOrderAsync();

        Assert.True(await Connections().RemoveAsync(_tenant, id, Ct));

        await using var after = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Empty(await after.PlatformConnections.ToListAsync(Ct));
        var kept = await after.Orders.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((order.Id, order.InternalStatus, order.CreatedAt), (kept.Id, kept.InternalStatus, kept.CreatedAt));
        Assert.Equal(2, await after.OrderItems.CountAsync(o => o.OrderId == order.Id, Ct));
        Assert.Equal(id, (await after.SyncLogs.SingleAsync(Ct)).PlatformConnectionId);
        Assert.Equal(id, (await after.IntegrationErrors.SingleAsync(Ct)).PlatformConnectionId);
    }

    [Fact]
    public async Task RemovedConnection_IsNeverPolledOrBackfilledAgain_WhileTheOtherConnectionKeepsSyncing()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        var trendyol = await AddConnectionAsync(_tenant, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        await AddConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, "YS-STORE-1");

        Assert.True(await Connections().RemoveAsync(_tenant, trendyol, Ct));
        var current = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);
        var backfill = await Sync().BackfillCustomerAsync(_tenant, Ct);

        Assert.Equal(0, _trendyol.Fetches);
        Assert.Equal(1, _yemeksepeti.Fetches);
        Assert.Equal((1, 0), (current.ConnectionCount, current.FailedConnections));
        Assert.Equal(0, backfill.FailedConnections);
        Assert.DoesNotContain(current.Connections, c => c.Platform == nameof(FoodPlatform.TrendyolYemek));
    }

    [Fact]
    public async Task SamePlatformAndStore_CanBeConnectedAgain_AfterRemoval()
    {
        var service = Connections();
        var first = await service.CreateAsync(_tenant, Command(FoodPlatform.TrendyolYemek, "TY-STORE-1"), Ct);
        Assert.True(first.Succeeded);
        Assert.Equal("Duplicate", (await service.CreateAsync(_tenant, Command(FoodPlatform.TrendyolYemek, "TY-STORE-1"), Ct)).ErrorCode);

        Assert.True(await service.RemoveAsync(_tenant, first.Id!.Value, Ct));
        var again = await service.CreateAsync(_tenant, Command(FoodPlatform.TrendyolYemek, "TY-STORE-1"), Ct);

        Assert.True(again.Succeeded);
        Assert.NotEqual(first.Id, again.Id);
        var row = Assert.Single(await service.GetListAsync(_tenant, Ct));
        Assert.Equal((again.Id!.Value, FoodPlatform.TrendyolYemek, "TY-STORE-1", true), (row.Id, row.Platform, row.StoreId, row.IsActive));
    }

    [Fact]
    public async Task RepeatedStaleOrForeignRemoval_DeletesNothing()
    {
        var service = Connections();
        var removed = (await service.CreateAsync(_tenant, Command(FoodPlatform.TrendyolYemek, "TY-STORE-1"), Ct)).Id!.Value;
        var other = (await service.CreateAsync(_tenant, Command(FoodPlatform.Yemeksepeti, "YS-STORE-1"), Ct)).Id!.Value;
        var foreign = (await service.CreateAsync(_otherTenant, Command(FoodPlatform.TrendyolYemek, "TY-OTHER"), Ct)).Id!.Value;

        Assert.True(await service.RemoveAsync(_tenant, removed, Ct));
        // A second submission of the same confirmation.
        Assert.False(await service.RemoveAsync(_tenant, removed, Ct));
        // A confirmation opened before the platform was connected again carries the old id, never the new connection's.
        var reconnected = (await service.CreateAsync(_tenant, Command(FoodPlatform.TrendyolYemek, "TY-STORE-2"), Ct)).Id!.Value;
        Assert.False(await service.RemoveAsync(_tenant, removed, Ct));
        // Another tenant's connection id is not found in this tenant's database.
        Assert.False(await service.RemoveAsync(_tenant, foreign, Ct));

        Assert.Equal(new[] { other, reconnected }.Order(), (await service.GetListAsync(_tenant, Ct)).Select(c => c.Id).Order());
        Assert.Equal(foreign, Assert.Single(await service.GetListAsync(_otherTenant, Ct)).Id);
    }

    [Fact]
    public async Task ConnectionRemovedWhileItsSyncRuns_KeepsWhatWasStored_RecordsNoFailure_AndTheOtherConnectionStillSyncs()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        var trendyol = await AddConnectionAsync(_tenant, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        await AddConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        _trendyol.Orders = [Order("ty-during")];
        _yemeksepeti.Orders = [Order("ys-after", FoodPlatform.Yemeksepeti, "CREATED")];
        // The operator removes the Trendyol connection while the Worker is fetching its orders.
        _trendyol.DuringFetch = () => Connections().RemoveAsync(_tenant, trendyol, CancellationToken.None);

        var result = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal((2, 0, 2), (result.ConnectionCount, result.FailedConnections, result.InsertedCount));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var connection = await db.PlatformConnections.SingleAsync(Ct);
        Assert.Equal((FoodPlatform.Yemeksepeti, 0), (connection.Platform, connection.ConsecutiveFailures));
        Assert.Equal(["ty-during", "ys-after"], (await db.Orders.Select(o => o.ExternalOrderId).ToListAsync(Ct)).Order());
        Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
        Assert.Equal([connection.Id], await db.SyncLogs.Select(l => l.PlatformConnectionId).ToListAsync(Ct));

        // The next pass does not load the removed connection.
        _trendyol.Fetches = 0;
        Assert.Equal(0, (await Sync().SyncCustomerWithResultAsync(_tenant, Ct)).FailedConnections);
        Assert.Equal(0, _trendyol.Fetches);
    }

    [Fact]
    public async Task ConnectionRemovedBeforeItsTurnInThePass_IsSkippedWithoutAFetch()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        // The pass reads connections in the order they were stored, so Yemeksepeti is synced first.
        await AddConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, "YS-STORE-1");
        var trendyol = await AddConnectionAsync(_tenant, FoodPlatform.TrendyolYemek, "TY-STORE-1");
        _yemeksepeti.Orders = [Order("ys-1", FoodPlatform.Yemeksepeti, "CREATED")];
        _yemeksepeti.DuringFetch = () => Connections().RemoveAsync(_tenant, trendyol, CancellationToken.None);

        var result = await Sync().SyncCustomerWithResultAsync(_tenant, Ct);

        Assert.Equal(1, _yemeksepeti.Fetches);
        Assert.Equal(0, _trendyol.Fetches);
        Assert.Equal((2, 0, 1), (result.ConnectionCount, result.FailedConnections, result.InsertedCount));
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(FoodPlatform.Yemeksepeti, (await db.PlatformConnections.SingleAsync(Ct)).Platform);
        Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
    }

    public void Dispose() => _tenants.Dispose();

    private PlatformConnectionService Connections() =>
        new(_tenants, new PassthroughSecrets(), new CreatePlatformConnectionCommandValidator());

    private OrderSyncService Sync() =>
        new(
            _tenants,
            new IFoodPlatformClient[] { _trendyol, _yemeksepeti },
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            NullTenantChangeNotifier.Instance,
            NullLogger<OrderSyncService>.Instance);

    private async Task<Guid> AddConnectionAsync(Guid tenant, FoodPlatform platform, string storeId)
    {
        await using var db = await _tenants.CreateAsync(tenant, Ct);
        var connection = new PlatformConnection
        {
            Platform = platform,
            StoreId = storeId,
            EncryptedApiKey = "fake-encrypted-key",
            EncryptedApiSecret = "fake-encrypted-secret",
            IsActive = true,
            SyncIntervalSeconds = 0
        };
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync(Ct);
        return connection.Id;
    }

    private async Task<Order> SingleOrderAsync()
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(Ct);
    }

    private static CreatePlatformConnectionCommand Command(FoodPlatform platform, string storeId) =>
        new(platform, storeId, "api-key", "api-secret", true);

    private static ExternalOrderDto Order(string externalOrderId, FoodPlatform platform = FoodPlatform.TrendyolYemek, string status = "Created") =>
        new(
            Platform: platform,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: "N-" + externalOrderId,
            OrderedAtUtc: DateTime.UtcNow.AddMinutes(-3),
            CustomerName: "Test Customer",
            CustomerPhone: "5320000000",
            CustomerAddress: "Test Street 1",
            Subtotal: 150m,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: 150m,
            PaymentMethod: PaymentMethod.CreditCard,
            PaymentStatus: PaymentStatus.Paid,
            ExternalStatus: status,
            RawPayloadJson: "{}",
            Items:
            [
                new ExternalOrderItemDto(externalOrderId + "-1", "Lahmacun", 1, 100m, 100m, null, []),
                new ExternalOrderItemDto(externalOrderId + "-2", "Ayran", 1, 50m, 50m, null, [])
            ]);

    private sealed class FakeProvider(FoodPlatform platform) : IFoodPlatformClient
    {
        public IReadOnlyCollection<ExternalOrderDto> Orders { get; set; } = [];

        /// <summary>Runs once, inside the next fetch, before the orders are returned.</summary>
        public Func<Task>? DuringFetch { get; set; }

        public int Fetches { get; set; }

        public FoodPlatform Platform => platform;

        public TimeSpan? MaxFetchWindow => null;

        public async Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct)
        {
            Fetches++;
            var during = DuringFetch;
            DuringFetch = null;
            if (during is not null)
                await during();
            return Orders;
        }

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
        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PassthroughSecrets : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult((plaintext, 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult(encryptedBase64);
    }
}
