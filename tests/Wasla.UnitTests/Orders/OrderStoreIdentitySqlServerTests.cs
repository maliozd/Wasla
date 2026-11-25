using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Abstractions.Security;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.Integrations.Platform;
using Wasla.Integrations.Platform.Dtos;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-241 on a real SQL Server, with every caller on its own service, DbContext and connection pool as separate Web or
/// Worker processes would be: simultaneous actions on an order whose store is not the connection's, or is not recorded,
/// all fail without a provider call, a status change, a version raise or a notification. The order action claim
/// (WAS-104) still decides which caller checks the order; the others are refused as contested. Orders of the current
/// store keep their single winner in <see cref="OrderActionConcurrencySqlServerTests"/>.
/// </summary>
public sealed class OrderStoreIdentitySqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string NotConfirmed = "Orders.StoreNotConfirmed";
    private const string Contested = "Orders.InvalidStatusForAction";
    private const string ConnectionStore = "store-current";
    private const string ConnectionSupplier = "supplier-current";
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly RecordingProvider _provider = new();
    private readonly RecordingNotifier _notifier = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("store-previous", "supplier-current")]
    [InlineData("store-current", "supplier-previous")]
    [InlineData(null, null)]
    public async Task SimultaneousActionsOnAnOrderNotOfTheCurrentStore_CallNoProvider_AndChangeNothing(string? orderStoreId, string? orderSupplierId)
    {
        for (var round = 0; round < 3; round++)
        {
            var orderId = await SeedOrderAsync(OrderStatus.New, orderStoreId, orderSupplierId);
            var before = await ReadAsync(orderId);
            _provider.Reset();
            _notifier.Reset();

            var results = await RaceAsync(6, i => i % 2 == 0
                ? Instance($"approve-{round}-{i}").TryApproveAsync(_tenantId, orderId, Ct)
                : Instance($"reject-{round}-{i}").TryRejectAsync(_tenantId, orderId, 623, Ct));

            Assert.DoesNotContain(results, r => r.Succeeded);
            Assert.Contains(results, r => r.MessageKey == NotConfirmed);
            Assert.All(results, r => Assert.Contains(r.MessageKey, new[] { NotConfirmed, Contested }));
            Assert.Empty(_provider.Calls);
            var after = await ReadAsync(orderId);
            Assert.Equal(OrderStatus.New, after.InternalStatus);
            Assert.Equal(before.ChangeVersion, after.ChangeVersion);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Null(after.AcceptedAt);
            Assert.Null(after.CancelledAt);
            Assert.Null(after.CancellationOrigin);
            Assert.Equal(orderStoreId, after.StoreId);
            Assert.Equal(orderSupplierId, after.SupplierId);
            Assert.Empty(_notifier.Changes);
        }
    }

    [Fact]
    public async Task SimultaneousMarkReadyOnAnOrderOfThePreviousStore_CallsNoProviderAndKeepsItPreparing()
    {
        var orderId = await SeedOrderAsync(OrderStatus.Preparing, "store-previous", ConnectionSupplier);
        var before = await ReadAsync(orderId);
        _provider.Reset();
        _notifier.Reset();

        var results = await RaceAsync(6, i => Instance($"ready-{i}").MarkReadyForPickupAsync(_tenantId, orderId, Ct));

        Assert.DoesNotContain(results, r => r.Succeeded);
        Assert.All(results, r => Assert.Contains(r.MessageKey, new[] { NotConfirmed, Contested }));
        Assert.Empty(_provider.Calls);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Preparing, after.InternalStatus);
        Assert.Equal(before.ChangeVersion, after.ChangeVersion);
        Assert.Empty(_notifier.Changes);
    }

    private OrderActionService Instance(string name) =>
        new(database.Instance(name), [_provider], new FakeSecrets(), _notifier, NullLogger<OrderActionService>.Instance);

    /// <summary>Starts every caller at the same moment from its own thread and waits for all of them.</summary>
    private static async Task<OrderActionResult[]> RaceAsync(int callers, Func<int, Task<OrderActionResult>> call)
    {
        using var start = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers)
            .Select(i => Task.Run(async () =>
            {
                start.SignalAndWait(Ct);
                return await call(i);
            }, Ct))
            .ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<Order> ReadAsync(Guid orderId)
    {
        await using var db = database.Open();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task EnsureTenantRowsAsync()
    {
        await using var db = database.Open();
        if (!await db.TenantOperationalSettings.AnyAsync(s => s.Id == SettingsId, Ct))
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = SettingsId,
                OrderSyncEnabled = true,
                OperationalMode = TenantOperationalMode.Live,
                ReceiptPrintCopyCount = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        if (!await db.PlatformConnections.AnyAsync(c => c.Platform == FoodPlatform.TrendyolYemek, Ct))
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = Guid.NewGuid(),
                Platform = FoodPlatform.TrendyolYemek,
                StoreId = ConnectionStore,
                SupplierId = ConnectionSupplier,
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedOrderAsync(OrderStatus status, string? storeId, string? supplierId)
    {
        await EnsureTenantRowsAsync();
        await using var db = database.Open();
        var orderId = Guid.NewGuid();
        var externalId = $"STORE-{orderId:N}";
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = externalId,
            ExternalOrderCode = "TY-" + externalId[^12..],
            IdempotencyKey = OrderIngestion.IdempotencyKey(FoodPlatform.TrendyolYemek, externalId),
            StoreId = storeId,
            SupplierId = supplierId,
            InternalStatus = status,
            PlatformStatus = status == OrderStatus.New ? "Created" : "Picking",
            CustomerName = "Store Customer",
            CustomerPhone = "5000000000",
            CustomerAddress = "Synthetic Street 1",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
            AcceptedAt = status == OrderStatus.New ? null : DateTime.UtcNow.AddMinutes(-3),
            RawPayloadJson = "{}",
            ChangeVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    /// <summary>Records every lifecycle call; a short delay widens the window in which callers overlap.</summary>
    private sealed class RecordingProvider : IFoodPlatformClient
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public ProviderPlatform Platform => ProviderPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;
        public IReadOnlyList<string> Calls => _calls.ToList();

        public void Reset() => _calls.Clear();

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(ProviderConnectionContext connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([]);

        public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Act("accept");
        public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Act("invoiced");
        public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Act("shipped");
        public Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Act("delivered");
        public Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct) => Act("reject");

        private async Task Act(string action)
        {
            _calls.Enqueue(action);
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private sealed class RecordingNotifier : ITenantChangeNotifier
    {
        private readonly ConcurrentQueue<TenantChange> _changes = new();

        public IReadOnlyList<TenantChange> Changes => _changes.ToList();

        public void Reset() => _changes.Clear();

        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
        {
            _changes.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecrets : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult(("cipher:" + plaintext, 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult("plain:" + encryptedBase64);
    }
}
