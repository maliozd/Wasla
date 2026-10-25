using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Realtime;

/// <summary>A provider whose orders the test sets; it records actions and can fail them.</summary>
internal sealed class ScriptedPlatformClient(FoodPlatform platform) : IFoodPlatformClient
{
    private IReadOnlyCollection<ExternalOrderDto> _orders = [];

    public FoodPlatform Platform { get; } = platform;
    public TimeSpan? MaxFetchWindow => null;
    public bool FailActions { get; set; }
    public List<string> Actions { get; } = [];

    public void Returns(params ExternalOrderDto[] orders) => _orders = orders;

    public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
        Task.FromResult(_orders);

    public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Act("accept");
    public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Act("invoiced");
    public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Act("shipped");
    public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Act("delivered");
    public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) => Act("reject");

    private Task Act(string action)
    {
        Actions.Add(action);
        return FailActions ? throw new HttpRequestException("provider unavailable") : Task.CompletedTask;
    }
}

internal static class OrderChangeFixtures
{
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>An active connection for the platform and the operational settings row sync reads.</summary>
    public static void SeedSyncableTenant(Admin.RecordingTenantDbFactory tenants, Guid tenantId, FoodPlatform platform = FoodPlatform.TrendyolYemek) =>
        tenants.Seed(tenantId, db =>
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Platform = platform,
                StoreId = $"store-{tenantId:N}",
                EncryptedApiKey = "fake",
                EncryptedApiSecret = "fake",
                IsActive = true,
                SyncIntervalSeconds = 0
            });
            if (!db.TenantOperationalSettings.Any())
            {
                db.TenantOperationalSettings.Add(new TenantOperationalSettings
                {
                    Id = SettingsId,
                    OrderSyncEnabled = true,
                    AutoApproveNewOrders = false,
                    AutoPrintReceiptOnAutoApprove = false,
                    ReceiptPrintCopyCount = 1
                });
            }
        });

    /// <summary>A provider order; "Created" maps to New, "Picking" to Accepted, "Cancelled" to Cancelled.</summary>
    public static ExternalOrderDto External(string externalOrderId, string status = "Created", decimal total = 100m, FoodPlatform platform = FoodPlatform.TrendyolYemek) =>
        new(
            Platform: platform,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: $"TY-{externalOrderId}",
            OrderedAtUtc: DateTime.UtcNow.AddMinutes(-1),
            CustomerName: "Test Customer",
            CustomerPhone: "+905555555555",
            CustomerAddress: "Test Address",
            Subtotal: total,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: total,
            PaymentMethod: PaymentMethod.CreditCard,
            PaymentStatus: PaymentStatus.Paid,
            ExternalStatus: status,
            RawPayloadJson: "{}",
            Items: [new ExternalOrderItemDto($"{externalOrderId}-1", "Lahmacun", 1, total, total, null, [])]);

    /// <summary>The real sync service as the Worker composes it, with the given notifier and no automation.</summary>
    public static OrderSyncService Sync(ITenantDbContextFactory tenants, IFoodPlatformClient client, ITenantChangeNotifier notifier) =>
        new(
            tenants,
            [client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            NoAutoApprove.Instance,
            NoReceipts.Instance,
            notifier,
            NullLogger<OrderSyncService>.Instance);

    public static OrderActionService Actions(ITenantDbContextFactory tenants, IFoodPlatformClient client, ITenantChangeNotifier notifier) =>
        new(tenants, [client], notifier, NullLogger<OrderActionService>.Instance);

    public static async Task<Order> ReadOrderAsync(ITenantDbContextFactory tenants, Guid tenantId, string externalOrderId)
    {
        await using var db = await tenants.CreateAsync(tenantId, CancellationToken.None);
        return db.Orders.Single(o => o.ExternalOrderId == externalOrderId);
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public static readonly NoAutoApprove Instance = new();
        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public static readonly NoReceipts Instance = new();
        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }
}
