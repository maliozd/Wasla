using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Platform;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The Trendyol GO Meal webhook stage pilot racing checkpointed polling on a real SQL Server: a <c>created</c> event's
/// reconciliation and a polling pass, from separate contexts and connection pools, both read that the package has no
/// order and both insert it at once. One order is stored; auto-approve, the provider-accepted receipt and the new
/// order's live notification run once; neither side fails.
/// </summary>
public sealed class TrendyolGoMealWebhookPollingRaceSqlServerTests : IClassFixture<SqlServerTenantDatabase>
{
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private const string Seller = "107385";
    private const string Store = "1331";

    private readonly SqlServerTenantDatabase _database;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingReceipts _receipts = new();
    private readonly RecordingAutoApprove _autoApprove = new();

    public TrendyolGoMealWebhookPollingRaceSqlServerTests(SqlServerTenantDatabase database)
    {
        _database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WebhookReconciliationAndPollingInsertingTogether_StoreOneOrder_WithEffectsOnce()
    {
        var connectionId = await SeedAsync();
        var packageId = $"MEAL-RACE-{Guid.NewGuid():N}";
        var client = new FakeWindowedClient(Package(packageId));
        var options = Options.Create(new TrendyolGoMealWebhookPilotOptions
        {
            Enabled = true,
            TenantId = _tenantId,
            PlatformConnectionId = connectionId,
            SellerId = Seller,
            StoreId = Store,
            SecretSha256 = new string('0', 64)
        });

        await new TrendyolGoMealWebhookInbox(options, _database.Instance("api"), NullLogger<TrendyolGoMealWebhookInbox>.Instance, TimeProvider.System)
            .AcceptAsync("created", Encoding.UTF8.GetBytes(TrendyolGoMealWebhookReceiverTests.Created(packageId)), Ct);

        // Both reach their order INSERT having read that the order is missing; SQL Server decides the winner.
        var barrier = new OrderInsertBarrier(parties: 2);
        var webhook = new TrendyolGoMealWebhookProcessor(
                options,
                new InterceptedFactory(_database.ConnectionString("worker-webhook"), barrier),
                Sync("worker-webhook", client, barrier),
                new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
                NullLogger<TrendyolGoMealWebhookProcessor>.Instance,
                TimeProvider.System)
            .ProcessDueAsync(10, Ct);
        var polling = Sync("worker-polling", client, barrier).SyncCustomerWithResultAsync(_tenantId, Ct);
        await Task.WhenAll(webhook, polling).WaitAsync(Deadline, Ct);

        Assert.True(barrier.Released, "The webhook and the polling pass never reached their inserts together.");
        Assert.Equal(1, await webhook);
        Assert.Equal(0, (await polling).FailedConnections);

        await using var db = _database.Open();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == packageId, Ct);
        var stored = await db.ProviderWebhookEvents.AsNoTracking().SingleAsync(e => e.ExternalOrderId == packageId, Ct);
        Assert.Equal(ProviderWebhookEventStatus.Processed, stored.Status);
        Assert.Equal("Reconciled", stored.Outcome);
        Assert.Equal(order.Id, stored.OrderId);
        Assert.Equal(OrderStatus.Accepted, order.InternalStatus);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
        var connection = await db.PlatformConnections.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(0, connection.ConsecutiveFailures);
    }

    private async Task<Guid> SeedAsync()
    {
        await using var db = _database.Open();
        if (!await db.TenantOperationalSettings.AnyAsync(s => s.Id == SettingsId, Ct))
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = SettingsId,
                OrderSyncEnabled = true,
                OperationalMode = TenantOperationalMode.Live,
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
                StoreId = Store,
                SupplierId = Seller,
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                SyncIntervalSeconds = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(Ct);
        return (await db.PlatformConnections.AsNoTracking().SingleAsync(Ct)).Id;
    }

    private OrderSyncService Sync(string instance, IFoodPlatformClient client, IInterceptor barrier) =>
        new(
            new InterceptedFactory(_database.ConnectionString(instance), barrier),
            [client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            _autoApprove,
            _receipts,
            _notifier,
            NullLogger<OrderSyncService>.Instance);

    private static ExternalOrderDto Package(string packageId) =>
        new(
            FoodPlatform.TrendyolYemek,
            packageId,
            "946511491",
            DateTime.UtcNow.AddMinutes(-2),
            "Race Customer",
            "0000000000",
            "Synthetic Street 1",
            100m,
            0m,
            0m,
            100m,
            PaymentMethod.OnlinePayment,
            PaymentStatus.Paid,
            "Picking",
            "{}",
            [new ExternalOrderItemDto(packageId + "-line-1", "Synthetic drink", 1, 100m, 100m, null, [])])
        {
            ProviderSupplierId = Seller,
            ProviderStoreId = Store
        };

    /// <summary>Lets each order INSERT go only when every party has reached its own.</summary>
    private sealed class OrderInsertBarrier(int parties) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool Released => _all.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [Orders]", StringComparison.Ordinal) && !_all.Task.IsCompleted)
            {
                if (Interlocked.Increment(ref _arrived) >= parties)
                    _all.TrySetResult();
                await _all.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class InterceptedFactory(string connectionString, IInterceptor interceptor) : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct) =>
            Task.FromResult(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connectionString).AddInterceptors(interceptor).Options));
    }

    private sealed class FakeWindowedClient(ExternalOrderDto package) : IFoodPlatformClient
    {
        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => TimeSpan.FromHours(1);

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([package]);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingNotifier : ITenantChangeNotifier
    {
        private readonly ConcurrentQueue<TenantChange> _changes = new();

        public IReadOnlyList<long> VersionsFor(Guid orderId) =>
            _changes.OfType<OrderChanged>().Where(c => c.OrderId == orderId).Select(c => c.Version).ToList();

        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
        {
            _changes.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReceipts : IOrderReceiptCreationService
    {
        private readonly ConcurrentQueue<Guid> _created = new();
        public IReadOnlyList<Guid> Created => _created.ToList();

        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            _created.Enqueue(orderId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAutoApprove : IOrderAutoApproveService
    {
        private readonly ConcurrentQueue<Guid> _processed = new();
        public IReadOnlyList<Guid> Processed => _processed.ToList();

        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct)
        {
            _processed.Enqueue(orderId);
            return Task.CompletedTask;
        }
    }
}
