using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Orders;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Orders;

public sealed class OrderSyncUnchangedShortCircuitTests : IDisposable
{
    private const int RepeatCount = 5;

    private static readonly DateTime OrderedAt = new(2026, 9, 26, 8, 30, 0, DateTimeKind.Utc);

    private readonly WriteCommandCollector _writes = new();
    private readonly SqliteTenantFactory _db;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly FakeClient _client = new();

    public OrderSyncUnchangedShortCircuitTests()
    {
        _db = new SqliteTenantFactory(_writes);
    }

    [Fact]
    public async Task StableOrder_RepeatedSync_DoesNotRewriteRows()
    {
        var sync = await CreateSyncAsync();
        var baseline = Sample(rawPayloadJson: "{\"poll\":0}");

        var first = await SyncAsync(sync, baseline);
        Assert.Equal(1, first.InsertedCount);
        Assert.Equal(0, first.UpdatedCount);
        Assert.Equal(0, first.UnchangedCount);

        var afterInsert = await ReadAsync(baseline.ExternalOrderId);
        Assert.Equal("{\"poll\":0}", afterInsert.RawPayloadJson);
        Assert.Single(afterInsert.ItemIds);
        Assert.Equal(2, afterInsert.OptionIds.Length);

        _writes.Clear();
        OrderSyncCustomerResult? last = null;
        for (var i = 1; i < RepeatCount; i++)
        {
            var noisy = baseline with
            {
                Subtotal = 5_000m + i,
                RawPayloadJson = "{\"poll\":" + i + ",\"cursor\":\"page\"}",
                Items = baseline.Items
                    .Select(item => item with { ExternalItemId = "noise-" + i })
                    .ToArray()
            };

            last = await SyncAsync(sync, noisy);
            Assert.Equal(0, last.InsertedCount);
            Assert.Equal(0, last.UpdatedCount);
            Assert.Equal(0, last.SkippedCount);
            Assert.Equal(1, last.UnchangedCount);
            Assert.Equal(0, last.FailedConnections);
        }

        var afterRepeats = await ReadAsync(baseline.ExternalOrderId);
        Assert.Equal(afterInsert.Id, afterRepeats.Id);
        Assert.Equal(afterInsert.CreatedAt, afterRepeats.CreatedAt);
        Assert.Equal(afterInsert.UpdatedAt, afterRepeats.UpdatedAt);
        Assert.Equal(afterInsert.ItemIds, afterRepeats.ItemIds);
        Assert.Equal(afterInsert.OptionIds, afterRepeats.OptionIds);
        Assert.Equal(afterInsert.RawPayloadJson, afterRepeats.RawPayloadJson);
        Assert.Equal(OrderStatus.New, afterRepeats.InternalStatus);

        Assert.NotEmpty(_writes.Commands);
        Assert.Contains(_writes.Commands, sql => sql.Contains("SyncLogs", StringComparison.Ordinal));
        Assert.DoesNotContain(_writes.Commands, TouchesOrderTables);

        await using var db = await _db.CreateAsync(_tenantId, CancellationToken);
        var logs = await db.SyncLogs.ToListAsync(CancellationToken);
        Assert.Equal(RepeatCount, logs.Count);
        Assert.All(logs, log => Assert.Equal(SyncStatus.Success, log.Status));
        Assert.Equal(1, logs.Sum(log => log.OrdersInserted));
        Assert.Equal(0, logs.Sum(log => log.OrdersUpdated));
    }

    [Fact]
    public async Task NewOrder_IsInserted()
    {
        var sync = await CreateSyncAsync();
        var order = Sample(customerNote: "  Ring the bell  ");

        var result = await SyncAsync(sync, order);

        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);

        var stored = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.New, stored.InternalStatus);
        Assert.Equal("Created", stored.PlatformStatus);
        Assert.Equal("Ring the bell", stored.CustomerNote);
        Assert.Equal(120m, stored.TotalAmount);
        Assert.Single(stored.ItemIds);
        Assert.Equal(2, stored.OptionIds.Length);
    }

    [Fact]
    public async Task StatusChanged_UpdatesOrder()
    {
        var sync = await CreateSyncAsync();
        var created = Sample();
        await SyncAsync(sync, created);
        var before = await ReadAsync(created.ExternalOrderId);

        var result = await SyncAsync(sync, created with { ExternalStatus = "Picking" });

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
        var after = await ReadAsync(created.ExternalOrderId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.Equal("Picking", after.PlatformStatus);
        Assert.NotNull(after.AcceptedAt);
        Assert.NotEqual(before.ItemIds, after.ItemIds);
    }

    [Fact]
    public async Task TotalChanged_UpdatesOrder()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        var before = await ReadAsync(order.ExternalOrderId);

        var result = await SyncAsync(sync, order with { Total = 150m });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(150m, after.TotalAmount);
        Assert.NotEqual(before.ItemIds, after.ItemIds);
        Assert.NotEqual(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task CustomerNoteChanged_UpdatesOrder()
    {
        var sync = await CreateSyncAsync();
        var order = Sample(customerNote: "Ring the bell");
        await SyncAsync(sync, order);

        var result = await SyncAsync(sync, order with { CustomerNote = "  Leave at the door  " });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal("Leave at the door", after.CustomerNote);
    }

    [Fact]
    public async Task EquivalentBlankAndPaddedNotes_DoNotRewrite()
    {
        var sync = await CreateSyncAsync();
        var blank = Sample(
            externalOrderId: "blank",
            customerNote: "   ",
            items: [Item("Soup", 1, 40m, 40m, "   ", "soup")]);
        var padded = Sample(
            externalOrderId: "padded",
            customerNote: "  Ring the bell  ",
            items: [Item("Soup", 1, 40m, 40m, "  spicy  ", "soup")]);

        var inserted = await SyncAsync(sync, blank, padded);
        Assert.Equal(2, inserted.InsertedCount);

        var blankBefore = await ReadAsync("blank");
        var paddedBefore = await ReadAsync("padded");
        Assert.Null(blankBefore.CustomerNote);
        Assert.Equal("   ", blankBefore.Items[0].Notes);
        Assert.Equal("Ring the bell", paddedBefore.CustomerNote);
        Assert.Equal("  spicy  ", paddedBefore.Items[0].Notes);

        var blankAgain = blank with
        {
            CustomerNote = "",
            Items = [blank.Items.Single() with { Notes = null, ExternalItemId = "soup-2" }]
        };
        var paddedAgain = padded with
        {
            CustomerNote = "\tRing the bell\t",
            Subtotal = 1m,
            RawPayloadJson = "{\"different\":true}",
            Items = [padded.Items.Single() with { Notes = "spicy", ExternalItemId = "soup-3" }]
        };

        var result = await SyncAsync(sync, blankAgain, paddedAgain);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(2, result.UnchangedCount);

        var blankAfter = await ReadAsync("blank");
        var paddedAfter = await ReadAsync("padded");
        Assert.Equal(blankBefore.Id, blankAfter.Id);
        Assert.Equal(paddedBefore.Id, paddedAfter.Id);
        Assert.Equal(blankBefore.UpdatedAt, blankAfter.UpdatedAt);
        Assert.Equal(paddedBefore.UpdatedAt, paddedAfter.UpdatedAt);
        Assert.Equal(blankBefore.ItemIds, blankAfter.ItemIds);
        Assert.Equal(paddedBefore.ItemIds, paddedAfter.ItemIds);
        Assert.Equal(blankBefore.OptionIds, blankAfter.OptionIds);
        Assert.Null(blankAfter.CustomerNote);
        Assert.Equal("   ", blankAfter.Items[0].Notes);
        Assert.Equal("Ring the bell", paddedAfter.CustomerNote);
        Assert.Equal("  spicy  ", paddedAfter.Items[0].Notes);
        Assert.Equal(paddedBefore.RawPayloadJson, paddedAfter.RawPayloadJson);
    }

    [Fact]
    public async Task QuantityChanged_ReplacesItems()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        var before = await ReadAsync(order.ExternalOrderId);

        var changedItem = order.Items.Single() with { Quantity = 3, TotalPrice = 300m };
        var result = await SyncAsync(sync, order with { Items = [changedItem], Total = 320m });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(3, after.Items[0].Quantity);
        Assert.NotEqual(before.ItemIds, after.ItemIds);
        Assert.NotEqual(before.OptionIds, after.OptionIds);
    }

    [Fact]
    public async Task ItemNoteChanged_ReplacesItems()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        var before = await ReadAsync(order.ExternalOrderId);

        var changedItem = order.Items.Single() with { Notes = "extra sauce" };
        var result = await SyncAsync(sync, order with { Items = [changedItem] });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal("extra sauce", after.Items[0].Notes);
        Assert.NotEqual(before.ItemIds, after.ItemIds);
    }

    [Fact]
    public async Task ModifierChanged_ReplacesItems()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        var before = await ReadAsync(order.ExternalOrderId);

        var changedItem = order.Items.Single() with
        {
            Options = [new ExternalOrderItemOptionDto("Cheese", 9m), new ExternalOrderItemOptionDto("Spicy", 2m)]
        };
        var result = await SyncAsync(sync, order with { Items = [changedItem] });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Contains(after.Items[0].Options, option => option.Name == "Cheese" && option.Price == 9m);
        Assert.NotEqual(before.ItemIds, after.ItemIds);
        Assert.NotEqual(before.OptionIds, after.OptionIds);
    }

    [Fact]
    public async Task ReorderedItemsAndModifiers_AreUnchanged()
    {
        var sync = await CreateSyncAsync();
        var cheese = new ExternalOrderItemOptionDto("Cheese", 5m);
        var bacon = new ExternalOrderItemOptionDto("Bacon", 7m);
        var ketchup = new ExternalOrderItemOptionDto("Ketchup", 2m);
        var burger = Item("Burger", 1, 80m, 80m, null, "burger", cheese, bacon);
        var fries = Item("Fries", 2, 20m, 40m, "extra salt", "fries", ketchup);
        var original = Sample(items: [burger, fries]);

        await SyncAsync(sync, original);
        var before = await ReadAsync(original.ExternalOrderId);

        var reordered = original with
        {
            Subtotal = 1m,
            RawPayloadJson = "{\"reordered\":true}",
            Items =
            [
                fries with { ExternalItemId = "fries-moved" },
                burger with
                {
                    ExternalItemId = "burger-moved",
                    Options = [bacon, cheese]
                }
            ]
        };

        var result = await SyncAsync(sync, reordered);

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(0, result.UpdatedCount);
        var after = await ReadAsync(original.ExternalOrderId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.ItemIds, after.ItemIds);
        Assert.Equal(before.OptionIds, after.OptionIds);
        Assert.Equal(before.RawPayloadJson, after.RawPayloadJson);
        Assert.Equal(2, after.Items.Length);
        Assert.Equal(3, after.OptionIds.Length);
    }

    [Fact]
    public async Task MoneyInsidePersistedCent_DoesNotRewrite_NextCentDoes()
    {
        var sync = await CreateSyncAsync();
        var order = Sample(total: 120.00m);
        await SyncAsync(sync, order);
        var before = await ReadAsync(order.ExternalOrderId);

        var withinCent = await SyncAsync(sync, order with { Total = 120.004m });
        Assert.Equal(1, withinCent.UnchangedCount);
        var still = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(before.UpdatedAt, still.UpdatedAt);
        Assert.Equal(before.ItemIds, still.ItemIds);
        Assert.Equal(120.00m, still.TotalAmount);

        var nextCent = await SyncAsync(sync, order with { Total = 120.005m });
        Assert.Equal(1, nextCent.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(120.005m, after.TotalAmount);
        Assert.NotEqual(before.ItemIds, after.ItemIds);
    }

    [Fact]
    public async Task SynchronizedScalarFields_UpdateWhenChanged()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        var id = (await ReadAsync(order.ExternalOrderId)).Id;

        await AssertUpdatedAsync(sync, order with { DeliveryFee = 18m }, stored => Assert.Equal(18m, stored.DeliveryFee));
        await AssertUpdatedAsync(sync, order with { DeliveryFee = 18m, ServiceFee = 6m }, stored => Assert.Equal(6m, stored.ServiceFee));
        await AssertUpdatedAsync(
            sync,
            order with { DeliveryFee = 18m, ServiceFee = 6m, PaymentMethod = PaymentMethod.Cash },
            stored => Assert.Equal(PaymentMethod.Cash, stored.PaymentMethod));
        await AssertUpdatedAsync(
            sync,
            order with { DeliveryFee = 18m, ServiceFee = 6m, PaymentMethod = PaymentMethod.Cash, PaymentStatus = PaymentStatus.Pending },
            stored => Assert.Equal(PaymentStatus.Pending, stored.PaymentStatus));
        var later = OrderedAt.AddMinutes(5);
        await AssertUpdatedAsync(
            sync,
            order with { DeliveryFee = 18m, ServiceFee = 6m, PaymentMethod = PaymentMethod.Cash, PaymentStatus = PaymentStatus.Pending, OrderedAtUtc = later },
            stored => Assert.Equal(later, stored.CreatedAtPlatform));
        await AssertUpdatedAsync(
            sync,
            order with
            {
                DeliveryFee = 18m,
                ServiceFee = 6m,
                PaymentMethod = PaymentMethod.Cash,
                PaymentStatus = PaymentStatus.Pending,
                OrderedAtUtc = later,
                CustomerName = "Mehmet Demir"
            },
            stored => Assert.Equal("Mehmet Demir", stored.CustomerName));
        await AssertUpdatedAsync(
            sync,
            order with
            {
                DeliveryFee = 18m,
                ServiceFee = 6m,
                PaymentMethod = PaymentMethod.Cash,
                PaymentStatus = PaymentStatus.Pending,
                OrderedAtUtc = later,
                CustomerName = "Mehmet Demir",
                CustomerPhone = "5550000000"
            },
            stored => Assert.Equal("5550000000", stored.CustomerPhone));
        await AssertUpdatedAsync(
            sync,
            order with
            {
                DeliveryFee = 18m,
                ServiceFee = 6m,
                PaymentMethod = PaymentMethod.Cash,
                PaymentStatus = PaymentStatus.Pending,
                OrderedAtUtc = later,
                CustomerName = "Mehmet Demir",
                CustomerPhone = "5550000000",
                CustomerAddress = "Bagdat Cad. No:2"
            },
            stored => Assert.Equal("Bagdat Cad. No:2", stored.CustomerAddress));
        await AssertUpdatedAsync(
            sync,
            order with
            {
                DeliveryFee = 18m,
                ServiceFee = 6m,
                PaymentMethod = PaymentMethod.Cash,
                PaymentStatus = PaymentStatus.Pending,
                OrderedAtUtc = later,
                CustomerName = "Mehmet Demir",
                CustomerPhone = "5550000000",
                CustomerAddress = "Bagdat Cad. No:2",
                ExternalOrderCode = "TY-CHANGED"
            },
            stored =>
            {
                Assert.Equal("TY-CHANGED", stored.ExternalOrderCode);
                Assert.Equal(id, stored.Id);
            });
    }

    [Fact]
    public async Task UnchangedProviderPayload_KeepsOperatorStatusAndRows()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        await SetStatusAsync(order.ExternalOrderId, OrderStatus.Preparing);
        var before = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.Preparing, before.InternalStatus);

        var result = await SyncAsync(sync, order);

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(0, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.Preparing, after.InternalStatus);
        Assert.Equal("Created", after.PlatformStatus);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.ItemIds, after.ItemIds);
        Assert.Equal(before.OptionIds, after.OptionIds);
    }

    [Fact]
    public async Task ProviderStatusTextChange_DoesNotRegressOperatorStatus()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        await SetStatusAsync(order.ExternalOrderId, OrderStatus.Preparing);

        var result = await SyncAsync(sync, order with { ExternalStatus = "Yeni" });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.Preparing, after.InternalStatus);
        Assert.Equal("Yeni", after.PlatformStatus);
    }

    [Fact]
    public async Task ProviderCancellation_OverridesInProgressStatus()
    {
        var sync = await CreateSyncAsync();
        var order = Sample();
        await SyncAsync(sync, order);
        await SetStatusAsync(order.ExternalOrderId, OrderStatus.Preparing);

        var result = await SyncAsync(sync, order with { ExternalStatus = "Cancelled" });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal("Cancelled", after.PlatformStatus);
        Assert.NotNull(after.CancelledAt);
    }

    [Fact]
    public async Task DeliveredOrder_IgnoresProviderCancellation()
    {
        var sync = await CreateSyncAsync();
        var delivered = Sample(externalStatus: "Delivered");
        await SyncAsync(sync, delivered);
        var before = await ReadAsync(delivered.ExternalOrderId);
        Assert.Equal(OrderStatus.Delivered, before.InternalStatus);
        Assert.NotNull(before.DeliveredAt);

        var result = await SyncAsync(sync, delivered with { ExternalStatus = "Cancelled" });

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
        var after = await ReadAsync(delivered.ExternalOrderId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(OrderStatus.Delivered, after.InternalStatus);
        Assert.Equal("Cancelled", after.PlatformStatus);
        Assert.Equal(before.DeliveredAt, after.DeliveredAt);
        Assert.Null(after.CancelledAt);
    }

    [Fact]
    public async Task CancelledOrder_IsNotReopenedByProviderRefresh()
    {
        var sync = await CreateSyncAsync();
        var cancelled = Sample(externalStatus: "Cancelled");
        await SyncAsync(sync, cancelled);

        var result = await SyncAsync(sync, cancelled with { ExternalStatus = "Created" });

        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(cancelled.ExternalOrderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal("Created", after.PlatformStatus);
        Assert.NotNull(after.CancelledAt);
    }

    [Fact]
    public async Task CourierSteps_ComeFromTheProvider_NotFromRestaurantCommands()
    {
        var sync = await CreateSyncAsync();
        var order = Sample(externalStatus: "Invoiced");
        await SyncAsync(sync, order);
        var ready = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.ReadyForPickup, ready.InternalStatus);
        var actions = new OrderActionService(_db, [_client], NullLogger<OrderActionService>.Instance);

        // Pickup: refused before any provider call or status change; provider sync applies it.
        var manualPickup = await actions.MarkOnTheWayAsync(_tenantId, ready.Id, CancellationToken);
        Assert.False(manualPickup.Succeeded);
        Assert.Equal(OrderDeliveryPolicy.UserPickupNotAllowedKey, manualPickup.MessageKey);
        Assert.Equal(0, _client.MarkShippedCalls);
        Assert.Equal(OrderStatus.ReadyForPickup, (await ReadAsync(order.ExternalOrderId)).InternalStatus);

        Assert.Equal(1, (await SyncAsync(sync, order with { ExternalStatus = "Shipped" })).UpdatedCount);
        var onTheWay = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.OnTheWay, onTheWay.InternalStatus);

        // Delivery: same rule.
        var manualDelivery = await actions.MarkDeliveredAsync(_tenantId, onTheWay.Id, CancellationToken);
        Assert.False(manualDelivery.Succeeded);
        Assert.Equal(OrderDeliveryPolicy.UserDeliveryNotAllowedKey, manualDelivery.MessageKey);
        Assert.Equal(0, _client.MarkDeliveredCalls);
        var refused = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(OrderStatus.OnTheWay, refused.InternalStatus);
        Assert.Null(refused.DeliveredAt);

        Assert.Equal(1, (await SyncAsync(sync, order with { ExternalStatus = "Delivered" })).UpdatedCount);
        var delivered = await ReadAsync(order.ExternalOrderId);
        Assert.Equal(ready.Id, delivered.Id);
        Assert.Equal(OrderStatus.Delivered, delivered.InternalStatus);
        Assert.NotNull(delivered.DeliveredAt);
    }

    public void Dispose() => _db.Dispose();

    private CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private async Task<OrderSyncService> CreateSyncAsync()
    {
        var ct = CancellationToken;
        await using var db = await _db.CreateAsync(_tenantId, ct);
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
        await db.SaveChangesAsync(ct);

        return new OrderSyncService(
            _db,
            [_client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            NullLogger<OrderSyncService>.Instance);
    }

    private async Task<OrderSyncCustomerResult> SyncAsync(OrderSyncService sync, params ExternalOrderDto[] orders)
    {
        _client.Set(orders);
        return await sync.SyncCustomerWithResultAsync(_tenantId, CancellationToken);
    }

    private async Task SetStatusAsync(string externalOrderId, OrderStatus status)
    {
        await using var db = await _db.CreateAsync(_tenantId, CancellationToken);
        var order = await db.Orders.SingleAsync(o => o.ExternalOrderId == externalOrderId, CancellationToken);
        order.InternalStatus = status;
        await db.SaveChangesAsync(CancellationToken);
    }

    private async Task AssertUpdatedAsync(
        OrderSyncService sync,
        ExternalOrderDto order,
        Action<StoredOrder> assert)
    {
        var result = await SyncAsync(sync, order);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
        assert(await ReadAsync(order.ExternalOrderId));
    }

    private async Task<StoredOrder> ReadAsync(string externalOrderId)
    {
        await using var db = await _db.CreateAsync(_tenantId, CancellationToken);
        var order = await db.Orders
            .Include(o => o.Items)
            .ThenInclude(i => i.Options)
            .SingleAsync(o => o.ExternalOrderId == externalOrderId, CancellationToken);

        var items = order.Items
            .OrderBy(i => i.ProductName, StringComparer.Ordinal)
            .ThenBy(i => i.Quantity)
            .Select(i => new StoredItem(
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                i.Options
                    .OrderBy(o => o.Name, StringComparer.Ordinal)
                    .ThenBy(o => o.Price)
                    .Select(o => new StoredOption(o.Name, o.Price))
                    .ToArray()))
            .ToArray();

        return new StoredOrder(
            order.Id,
            order.CreatedAt,
            order.UpdatedAt,
            order.CreatedAtPlatform,
            order.InternalStatus,
            order.PlatformStatus,
            order.ExternalOrderCode,
            order.CustomerName,
            order.CustomerPhone,
            order.CustomerAddress,
            order.CustomerNote,
            order.TotalAmount,
            order.DeliveryFee,
            order.ServiceFee,
            order.PaymentMethod,
            order.PaymentStatus,
            order.RawPayloadJson,
            order.AcceptedAt,
            order.DeliveredAt,
            order.CancelledAt,
            order.Items.Select(i => i.Id).OrderBy(id => id).ToArray(),
            order.Items.SelectMany(i => i.Options).Select(o => o.Id).OrderBy(id => id).ToArray(),
            items);
    }

    private static ExternalOrderDto Sample(
        string externalOrderId = "ext-1",
        string externalStatus = "Created",
        decimal total = 120m,
        decimal deliveryFee = 15m,
        decimal serviceFee = 5m,
        string? customerNote = "Ring the bell",
        string rawPayloadJson = """{"id":"ext-1"}""",
        decimal subtotal = 100m,
        DateTime? orderedAtUtc = null,
        PaymentMethod paymentMethod = PaymentMethod.CreditCard,
        PaymentStatus paymentStatus = PaymentStatus.Paid,
        IReadOnlyCollection<ExternalOrderItemDto>? items = null)
    {
        items ??=
        [
            Item("Lahmacun", 1, 100m, 100m, "No onions", externalOrderId + "-item",
                new ExternalOrderItemOptionDto("Cheese", 5m),
                new ExternalOrderItemOptionDto("Spicy", 2m))
        ];

        return new ExternalOrderDto(
            FoodPlatform.TrendyolYemek,
            externalOrderId,
            "TY-" + externalOrderId,
            orderedAtUtc ?? OrderedAt,
            "Ayse Yilmaz",
            "5313241245",
            "Istiklal Cad. No:1",
            subtotal,
            deliveryFee,
            serviceFee,
            total,
            paymentMethod,
            paymentStatus,
            externalStatus,
            rawPayloadJson,
            items,
            customerNote);
    }

    private static ExternalOrderItemDto Item(
        string name,
        int quantity,
        decimal unitPrice,
        decimal totalPrice,
        string? notes,
        string externalItemId,
        params ExternalOrderItemOptionDto[] options) =>
        new(externalItemId, name, quantity, unitPrice, totalPrice, notes, options);

    private static bool TouchesOrderTables(string sql) =>
        sql.Contains("\"Orders\"", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("\"OrderItems\"", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("\"OrderItemOptions\"", StringComparison.OrdinalIgnoreCase);

    private sealed record StoredOrder(
        Guid Id,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime CreatedAtPlatform,
        OrderStatus InternalStatus,
        string PlatformStatus,
        string ExternalOrderCode,
        string CustomerName,
        string CustomerPhone,
        string CustomerAddress,
        string? CustomerNote,
        decimal TotalAmount,
        decimal DeliveryFee,
        decimal ServiceFee,
        PaymentMethod PaymentMethod,
        PaymentStatus PaymentStatus,
        string RawPayloadJson,
        DateTime? AcceptedAt,
        DateTime? DeliveredAt,
        DateTime? CancelledAt,
        Guid[] ItemIds,
        Guid[] OptionIds,
        StoredItem[] Items);

    private sealed record StoredItem(
        string ProductName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string? Notes,
        StoredOption[] Options);

    private sealed record StoredOption(string Name, decimal Price);

    private sealed class FakeClient : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(_orders);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public int MarkShippedCalls { get; private set; }

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
        {
            MarkShippedCalls++;
            return Task.CompletedTask;
        }

        public int MarkDeliveredCalls { get; private set; }

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct)
        {
            MarkDeliveredCalls++;
            return Task.CompletedTask;
        }

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

    private sealed class WriteCommandCollector : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public void Clear() => Commands.Clear();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            var sql = command.CommandText ?? string.Empty;
            if (WriteStatement.IsMatch(sql))
                Commands.Add(sql);
        }

        private static readonly Regex WriteStatement = new(
            @"(?:^\s*|;\s*)(INSERT|UPDATE|DELETE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    }

    private sealed class SqliteTenantFactory : ITenantDbContextFactory, IDisposable
    {
        private readonly WriteCommandCollector _writes;
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public SqliteTenantFactory(WriteCommandCollector writes) => _writes = writes;

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                _connections[customerId] = connection;
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

        private DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(_writes)
                .Options;

        private sealed class TestTenantDbContext : TenantDbContext
        {
            public TestTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
            {
            }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<TenantOperationalSettings>(builder =>
                {
                    builder.ToTable("TenantOperationalSettings");
                    builder.HasKey(x => x.Id);
                });

                modelBuilder.Entity<PlatformConnection>(builder =>
                {
                    builder.ToTable("PlatformConnections");
                    builder.HasKey(x => x.Id);
                });

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
                    builder.HasMany(x => x.Options).WithOne(x => x.OrderItem).HasForeignKey(x => x.OrderItemId);
                });

                modelBuilder.Entity<OrderItemOption>(builder =>
                {
                    builder.ToTable("OrderItemOptions");
                    builder.HasKey(x => x.Id);
                });

                modelBuilder.Entity<SyncLog>(builder =>
                {
                    builder.ToTable("SyncLogs");
                    builder.HasKey(x => x.Id);
                });

                modelBuilder.Entity<IntegrationError>(builder =>
                {
                    builder.ToTable("IntegrationErrors");
                    builder.HasKey(x => x.Id);
                });
            }
        }
    }
}
