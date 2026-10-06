using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Printing;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// While a tenant is in Setup, provider orders are still synchronized, stored idempotently and listed, but nothing is
/// accepted at the provider or printed automatically. The decision is taken when the trigger happens: going live
/// replays nothing, and only orders that become eligible after that follow the normal automation settings. The guard
/// sits in the shared side-effect services, which any other ingestion path (e.g. a future webhook) also goes through.
/// </summary>
public sealed class SetupModeOrderSideEffectsTests : IDisposable
{
    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly SqliteConnection _centralConnection = new("Data Source=:memory:");
    private readonly RecordingApprovals _approvals;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly FakeProvider _provider = new();

    public SetupModeOrderSideEffectsTests()
    {
        _approvals = new RecordingApprovals(_tenants);
        _centralConnection.Open();
        using var central = Central();
        central.Database.EnsureCreated();
        central.Tenants.Add(new Tenant
        {
            Id = _tenant,
            Name = "Setup Mode Restaurant",
            Slug = "setup-mode",
            PrimaryDomain = "setup-mode.wasla.local",
            DatabaseName = "unused",
            EncryptedConnectionString = "unused",
            EncryptionKeyVersion = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        central.SaveChanges();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InSetup_ProviderOrdersAreStillSynchronized_StoredOnce_AndListedInOrders()
    {
        await SeedTenantAsync(TenantOperationalMode.Setup, autoApprove: true, autoPrint: true);
        _provider.Orders = [ProviderOrder("setup-1", "Created"), ProviderOrder("setup-2", "Picking")];

        await Sync().SyncCustomerAsync(_tenant, Ct);
        await Sync().SyncCustomerAsync(_tenant, Ct);

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(new[] { "setup-1", "setup-2" }, (await db.Orders.Select(o => o.ExternalOrderId).ToListAsync(Ct)).Order(StringComparer.Ordinal).ToArray());
        var listed = await new OrderReadService(_tenants, TimeProvider.System, NullLogger<OrderReadService>.Instance)
            .GetListAsync(_tenant, null, null, null, null, "receivedAt", "desc", 1, 50, null, Ct);
        Assert.Equal(2, listed.TotalCount);
    }

    [Fact]
    public async Task InSetup_NewOrdersAreNotAcceptedAutomatically()
    {
        await SeedTenantAsync(TenantOperationalMode.Setup, autoApprove: true, autoPrint: true);
        _provider.Orders = [ProviderOrder("setup-new", "Created")];

        await Sync().SyncCustomerAsync(_tenant, Ct);

        Assert.Empty(_approvals.Approved);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(OrderStatus.New, (await db.Orders.SingleAsync(Ct)).InternalStatus);
    }

    [Fact]
    public async Task InSetup_NoReceiptIsPrintedAutomatically_WhoeverAcceptsTheOrder()
    {
        await SeedTenantAsync(TenantOperationalMode.Setup, autoApprove: true, autoPrint: true);
        _provider.Orders = [ProviderOrder("accepted-at-provider", "Picking"), ProviderOrder("accepted-here", "Created")];

        await Sync().SyncCustomerAsync(_tenant, Ct);
        // The automatic receipt after an operator's approval goes through the same service.
        var acceptedHere = await OrderIdAsync("accepted-here");
        await ReceiptCreation().TryCreateOnOrderAcceptedAsync(_tenant, acceptedHere, Ct);

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task InSetup_ManualPrintingStillWorks()
    {
        await SeedTenantAsync(TenantOperationalMode.Setup, autoApprove: false, autoPrint: false);
        _provider.Orders = [ProviderOrder("manual-print", "Created")];
        await Sync().SyncCustomerAsync(_tenant, Ct);

        var manual = new ManualOrderPrintService(_tenants, ReceiptJobs(), printJobHistory: null!, NullLogger<ManualOrderPrintService>.Instance);
        var result = await manual.QueueReceiptPrintAsync(_tenant, await OrderIdAsync("manual-print"), "Setup Mode Restaurant", Ct);

        Assert.True(result.Success);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(1, await db.PrintJobs.CountAsync(Ct));
    }

    [Fact]
    public async Task GoingLive_ReplaysNothing_AndOnlyLaterOrdersFollowTheAutomation()
    {
        await SeedTenantAsync(TenantOperationalMode.Setup, autoApprove: true, autoPrint: true);
        _provider.Orders = [ProviderOrder("during-setup-new", "Created"), ProviderOrder("during-setup-accepted", "Picking")];
        await Sync().SyncCustomerAsync(_tenant, Ct);

        // The trainee skips: the tenant goes live.
        await new GuidedSetupService(_tenants, TimeProvider.System).SkipAsync(_tenant, _owner, Ct);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));

        // The same provider orders again (unchanged): nothing is accepted or printed for them, now or later.
        await Sync().SyncCustomerAsync(_tenant, Ct);
        Assert.Empty(_approvals.Approved);
        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
            Assert.Equal(0, await db.PrintJobs.CountAsync(Ct));

        // A genuinely new order after going live is accepted and printed as the settings say.
        _provider.Orders = [.. _provider.Orders, ProviderOrder("after-live", "Created")];
        await Sync().SyncCustomerAsync(_tenant, Ct);

        var afterLive = await OrderIdAsync("after-live");
        Assert.Equal(new[] { afterLive }, _approvals.Approved);
        await using var check = await _tenants.CreateAsync(_tenant, Ct);
        var job = await check.PrintJobs.SingleAsync(Ct);
        Assert.Equal(afterLive, job.OrderId);
    }

    [Fact]
    public async Task ALiveTenant_KeepsItsNormalAutomation()
    {
        await SeedTenantAsync(TenantOperationalMode.Live, autoApprove: true, autoPrint: true);
        _provider.Orders = [ProviderOrder("live-new", "Created")];

        await Sync().SyncCustomerAsync(_tenant, Ct);

        Assert.Single(_approvals.Approved);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(1, await db.PrintJobs.CountAsync(Ct));
    }

    /// <summary>What the Live Screen and the settings pages show is what the automation services actually do.</summary>
    [Theory]
    [InlineData(TenantOperationalMode.Setup)]
    [InlineData(TenantOperationalMode.Live)]
    public async Task TheEffectiveStatus_MatchesWhatTheAutomationDoes(TenantOperationalMode mode)
    {
        await SeedTenantAsync(mode, autoApprove: true, autoPrint: true);
        _provider.Orders = [ProviderOrder("status-check", "Created")];

        var status = await new TenantOperationalModeService(_tenants).GetAutomationStatusAsync(_tenant, Ct);
        await Sync().SyncCustomerAsync(_tenant, Ct);

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(AutomationState.Active, status.OrderSync);
        Assert.Single(await db.Orders.ToListAsync(Ct));
        Assert.Equal(status.AutoApprove == AutomationState.Active, _approvals.Approved.Count == 1);
        Assert.Equal(status.AutoReceipt == AutomationState.Active, await db.PrintJobs.CountAsync(Ct) == 1);
        Assert.Equal(mode == TenantOperationalMode.Setup ? AutomationState.PendingSetup : AutomationState.Active, status.AutoApprove);
    }

    [Fact]
    public async Task ALiveTenant_KeepsItsAutomation_WhileAnOwnerIsInOrderTraining()
    {
        await SeedTenantAsync(TenantOperationalMode.Live, autoApprove: true, autoPrint: true);
        // An Owner of the live restaurant is in order training: only their own Live Screen is isolated.
        await new GuidedSetupService(_tenants, TimeProvider.System).StartAsync(
            _tenant, _owner, new Wasla.Application.Abstractions.GuidedSetup.GuidedSetupPosition(
                Wasla.Application.GuidedSetup.GuidedSetupSections.LiveScreenDemo, Wasla.Application.GuidedSetup.GuidedTrainingSteps.Intro), Ct);
        _provider.Orders = [ProviderOrder("while-training", "Created")];

        await Sync().SyncCustomerAsync(_tenant, Ct);

        var order = await OrderIdAsync("while-training");
        Assert.Equal(new[] { order }, _approvals.Approved);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(order, (await db.PrintJobs.SingleAsync(Ct)).OrderId);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    public void Dispose()
    {
        _tenants.Dispose();
        _centralConnection.Dispose();
    }

    private OrderSyncService Sync() =>
        new(
            _tenants,
            new IFoodPlatformClient[] { _provider },
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new OrderAutoApproveService(_tenants, _approvals, ReceiptCreation(), NullLogger<OrderAutoApproveService>.Instance),
            ReceiptCreation(),
            NullLogger<OrderSyncService>.Instance);

    private OrderReceiptCreationService ReceiptCreation() =>
        new(_tenants, Central(), ReceiptJobs(), NullLogger<OrderReceiptCreationService>.Instance);

    private ReceiptPrintJobService ReceiptJobs() =>
        new(_tenants, new DefaultTemplates(), NullLogger<ReceiptPrintJobService>.Instance);

    private CentralDbContext Central() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_centralConnection).Options);

    private async Task SeedTenantAsync(TenantOperationalMode mode, bool autoApprove, bool autoPrint)
    {
        await _tenants.SeedSettingsAsync(_tenant, mode, autoApprove, autoPrint);
        await _tenants.SeedUserAsync(_tenant, _owner);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Id = Guid.NewGuid(),
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "store-setup-mode",
            EncryptedApiKey = "encrypted",
            EncryptedApiSecret = "encrypted",
            IsActive = true,
            SyncIntervalSeconds = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> OrderIdAsync(string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        return await db.Orders.Where(o => o.ExternalOrderId == externalOrderId).Select(o => o.Id).SingleAsync(Ct);
    }

    private static ExternalOrderDto ProviderOrder(string externalOrderId, string externalStatus) =>
        new(
            Platform: FoodPlatform.TrendyolYemek,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: $"TY-{externalOrderId}",
            OrderedAtUtc: DateTime.UtcNow.AddMinutes(-2),
            CustomerName: "Test Customer",
            CustomerPhone: "+905555555555",
            CustomerAddress: "Test Address",
            Subtotal: 100m,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: 100m,
            PaymentMethod: PaymentMethod.CreditCard,
            PaymentStatus: PaymentStatus.Paid,
            ExternalStatus: externalStatus,
            RawPayloadJson: "{}",
            Items: new[]
            {
                new ExternalOrderItemDto($"{externalOrderId}-item", "Lahmacun", 1, 100m, 100m, null, Array.Empty<ExternalOrderItemOptionDto>())
            });

    private sealed class FakeProvider : IFoodPlatformClient
    {
        public IReadOnlyCollection<ExternalOrderDto> Orders { get; set; } = [];

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(Orders);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            throw new InvalidOperationException("Accepting goes through IOrderActionService in these tests.");

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>Records automatic approvals (which would call the provider) and marks the order accepted locally.</summary>
    private sealed class RecordingApprovals(OperationalModeTestDatabases tenants) : IOrderActionService
    {
        public List<Guid> Approved { get; } = new();

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

    private sealed class DefaultTemplates : IReceiptTemplateSettingsService
    {
        public Task<ReceiptTemplateSettings> GetAsync(Guid customerId, string? customerDisplayName, string? defaultReceiptLanguage, CancellationToken ct) =>
            Task.FromResult(ReceiptTemplateSettings.CreateDefaults(customerDisplayName, defaultReceiptLanguage));

        public Task<ReceiptTemplateSettings> UpdateAsync(Guid customerId, string? customerDisplayName, UpdateReceiptTemplateSettingsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
