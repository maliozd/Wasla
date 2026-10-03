using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The Live Screen snapshot during order training. Training is Owner-only, and isolation is per user: whenever the
/// current Owner is in order training their snapshot leaves real orders out and carries only how many arrived since
/// they started, whatever the tenant's operational mode. Everyone else keeps the normal snapshot, and the tenant's mode
/// (which alone governs automation) never depends on it. Runs the real guided-setup and order-read services on SQLite.
/// </summary>
public sealed class LiveScreenTrainingIsolationTests : IDisposable
{
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);
    private static readonly TenantNavigationPermissions Kitchen = new(false, true, true, true, false, false, false, false, false, false);
    private static readonly GuidedSetupPosition TrainingStart = new(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.Intro);

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly OperationalModeTestClock _clock = new();
    private readonly FakeDemos _demos = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _secondOwner = Guid.NewGuid();
    private readonly Guid _cashier = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Scenario A: a new tenant (Setup) ------------------------------------------------------------------------

    [Fact]
    public async Task SetupTenant_TheTrainingOwner_SeesOnlyThePracticeOrder_AndHowManyRealOrdersArrived()
    {
        await TenantAsync(TenantOperationalMode.Setup);
        await Seed("before-training", OrderStatus.New, _clock.UtcNow.AddMinutes(-30));
        await GuidedSetup().StartAsync(_tenant, _owner, TrainingStart, Ct);
        await Seed("during-1", OrderStatus.New, _clock.UtcNow.AddMinutes(1));
        await Seed("during-2", OrderStatus.Cancelled, _clock.UtcNow.AddMinutes(2));
        _demos.ForLiveScreen = PracticeOrder();

        var snapshot = await LiveDataAsync(_owner, Owner);

        var order = Assert.Single(snapshot.Orders);
        Assert.True(order.IsDemo);
        // Counted from the Owner's own training start by the canonical received time: the earlier order does not count.
        Assert.Equal(2, snapshot.Training!.RealOrdersReceived);
        Assert.True(snapshot.Training.Isolated);
        Assert.Equal(3, (await Orders().GetListAsync(_tenant, null, null, null, null, "receivedAt", "desc", 1, 50, null, Ct)).TotalCount);
    }

    [Fact]
    public async Task SetupTenant_AfterTheOwnerCompletes_ActiveTrainingOrdersAppear_AndTerminalOnesStayInHistory()
    {
        await TenantAsync(TenantOperationalMode.Setup);
        await GuidedSetup().StartAsync(_tenant, _owner, TrainingStart, Ct);
        var newOrder = await Seed("t-new", OrderStatus.New, _clock.UtcNow.AddMinutes(1));
        var preparing = await Seed("t-preparing", OrderStatus.Preparing, _clock.UtcNow.AddMinutes(2));
        await Seed("t-cancelled", OrderStatus.Cancelled, _clock.UtcNow.AddMinutes(3));
        await Seed("t-delivered", OrderStatus.Delivered, _clock.UtcNow.AddMinutes(4), deliveredAt: _clock.UtcNow.AddMinutes(5));
        _clock.Now = _clock.Now.AddHours(1);
        Assert.Empty((await LiveDataAsync(_owner, Owner)).Orders);

        await CompleteTrainingAsync(_owner);

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        var live = await LiveDataAsync(_owner, Owner);
        Assert.Null(live.Training);
        Assert.Equal(new[] { newOrder, preparing }.Order(), live.Orders.Select(o => o.Id).Order());
        Assert.Equal(4, (await Orders().GetListAsync(_tenant, null, null, null, null, "receivedAt", "desc", 1, 50, null, Ct)).TotalCount);
    }

    [Fact]
    public async Task SetupTenant_TheOwnersSkip_TakesItLive_AndOpensTheNormalLiveScreen()
    {
        await TenantAsync(TenantOperationalMode.Setup);
        await Seed("waiting", OrderStatus.New, _clock.UtcNow.AddMinutes(1));

        var skipped = await Coordinator(Owner).SkipAsync(_tenant, _owner, Principal(_owner), Ct);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, skipped.RedirectUrl);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        var snapshot = await LiveDataAsync(_owner, Owner);
        Assert.Single(snapshot.Orders);
        Assert.Null(snapshot.Training);
    }

    // Scenario B: an already-live tenant, another Owner trains ----------------------------------------------

    [Fact]
    public async Task LiveTenant_AnotherOwnerWhoStartsTraining_IsIsolated_WhileTheTenantStaysLive()
    {
        await TenantAsync(TenantOperationalMode.Live);
        await Seed("active-before", OrderStatus.Preparing, _clock.UtcNow.AddMinutes(-10));

        await Coordinator(Owner).StartAsync(_tenant, _secondOwner, Principal(_secondOwner), Ct);
        await GuidedSetup().SaveProgressAsync(_tenant, _secondOwner, TrainingStart, Ct);
        await Seed("during-training", OrderStatus.New, _clock.UtcNow.AddMinutes(1));
        _demos.ForLiveScreen = PracticeOrder();

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        var trainee = await LiveDataAsync(_secondOwner, Owner);
        Assert.True(Assert.Single(trainee.Orders).IsDemo, "only the practice order");
        Assert.Equal(1, trainee.Training!.RealOrdersReceived);

        // The rest of the restaurant keeps operating on the same orders.
        _demos.ForLiveScreen = null;
        var cashier = await LiveDataAsync(_cashier, Kitchen);
        Assert.Equal(2, cashier.Orders.Count);
        Assert.Null(cashier.Training);
        var firstOwner = await LiveDataAsync(_owner, Owner);
        Assert.Equal(2, firstOwner.Orders.Count);
        Assert.Null(firstOwner.Training);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveTenant_TheTrainingOwnersSkipOrCompletion_LeavesTheTenantLive(bool complete)
    {
        await TenantAsync(TenantOperationalMode.Live);
        await Coordinator(Owner).StartAsync(_tenant, _secondOwner, Principal(_secondOwner), Ct);
        await GuidedSetup().SaveProgressAsync(_tenant, _secondOwner, TrainingStart, Ct);
        await Seed("live-1", OrderStatus.New, _clock.UtcNow.AddMinutes(1));
        Assert.NotNull((await LiveDataAsync(_secondOwner, Owner)).Training);

        if (complete)
            await CompleteTrainingAsync(_secondOwner);
        else
            await Coordinator(Owner).EndAsync(_tenant, _secondOwner, Principal(_secondOwner), confirmed: true, Ct);

        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        var after = await LiveDataAsync(_secondOwner, Owner);
        Assert.Null(after.Training);
        Assert.Single(after.Orders);
    }

    // Both modes ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TenantOperationalMode.Setup)]
    [InlineData(TenantOperationalMode.Live)]
    public async Task InEitherMode_TheCountCoversOrdersSinceTheOwnerStarted_AndCarriesNoOrderDetail(TenantOperationalMode mode)
    {
        await TenantAsync(mode);
        await GuidedSetup().StartAsync(_tenant, _owner, TrainingStart, Ct);
        await Seed("secret-order", OrderStatus.New, _clock.UtcNow.AddMinutes(1), customer: "Ayşe Yılmaz");
        await Seed("second", OrderStatus.Delivered, _clock.UtcNow.AddMinutes(2), deliveredAt: _clock.UtcNow.AddMinutes(3));

        var json = JsonSerializer.Serialize(await LiveDataAsync(_owner, Owner), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        var training = document.RootElement.GetProperty("training");
        Assert.Equal(new[] { "isolated", "realOrdersReceived" }, training.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, training.GetProperty("realOrdersReceived").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("orders").GetArrayLength());
        Assert.DoesNotContain("Ayşe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-order", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TenantOperationalMode.Setup)]
    [InlineData(TenantOperationalMode.Live)]
    public async Task InEitherMode_AnotherUserOfTheSameTenant_SeesRealOrdersNormally(TenantOperationalMode mode)
    {
        await TenantAsync(mode);
        await GuidedSetup().StartAsync(_tenant, _owner, TrainingStart, Ct);
        await Seed("live-1", OrderStatus.New, _clock.UtcNow.AddMinutes(1));

        var snapshot = await LiveDataAsync(_cashier, Kitchen);

        Assert.Single(snapshot.Orders, o => !o.IsDemo);
        Assert.Null(snapshot.Training);
    }

    // Scenario C and boundaries --------------------------------------------------------------------------------

    [Fact]
    public async Task ANonOwnerWithAnEarlierTrainingRow_IsNotIsolated_AndGetsNoTrainingPanel()
    {
        await TenantAsync(TenantOperationalMode.Setup);
        // A Kitchen user who started order training before it became Owner-only.
        await GuidedSetup().StartAsync(_tenant, _cashier, TrainingStart, Ct);
        await Seed("live-1", OrderStatus.New, _clock.UtcNow.AddMinutes(1));

        var snapshot = await LiveDataAsync(_cashier, Kitchen);

        Assert.Single(snapshot.Orders);
        Assert.Null(snapshot.Training);
        Assert.Null(await Coordinator(Kitchen).GetLiveScreenAsync(_tenant, _cashier, Principal(_cashier), Ct));
    }

    [Fact]
    public async Task TrainingInOneTenant_NeverIsolatesTheSameUserInAnotherTenant()
    {
        await TenantAsync(TenantOperationalMode.Setup);
        await GuidedSetup().StartAsync(_tenant, _owner, TrainingStart, Ct);
        await _tenants.SeedSettingsAsync(_otherTenant, TenantOperationalMode.Setup);
        await _tenants.SeedUserAsync(_otherTenant, _owner);
        await Seed("other-tenant-order", OrderStatus.New, _clock.UtcNow.AddMinutes(1), tenantId: _otherTenant);

        var otherTenant = await LiveDataAsync(_owner, Owner, tenantId: _otherTenant);

        Assert.Single(otherTenant.Orders);
        Assert.Null(otherTenant.Training);
        Assert.NotNull((await LiveDataAsync(_owner, Owner)).Training);
    }

    [Fact]
    public async Task IfTrainingCannotBeRead_TheLiveScreenShowsEveryOrder()
    {
        await TenantAsync(TenantOperationalMode.Setup);
        await Seed("live-1", OrderStatus.New, _clock.UtcNow.AddMinutes(1));
        var logger = new CapturingLogger();
        var failing = new FakeGuidedSetup { FailReads = true };
        var coordinator = new GuidedSetupCoordinator(failing, new FixedNavigation(Owner), new FakeSetupStatus(), _demos,
            new FakePrintBridgeDevices(), logger);

        var snapshot = await LiveDataAsync(_owner, Owner, coordinator: coordinator);

        Assert.Single(snapshot.Orders);
        Assert.Null(snapshot.Training);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("isolation could not be read", StringComparison.Ordinal));
    }

    public void Dispose() => _tenants.Dispose();

    private async Task TenantAsync(TenantOperationalMode mode)
    {
        if (mode == TenantOperationalMode.Setup)
        {
            await using var db = await _tenants.CreateAsync(_tenant, CancellationToken.None);
            await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(db, _clock.UtcNow, CancellationToken.None);
        }
        else
        {
            await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        }

        await _tenants.SeedUserAsync(_tenant, _owner);
        await _tenants.SeedUserAsync(_tenant, _secondOwner);
        await _tenants.SeedUserAsync(_tenant, _cashier, UserRole.Kitchen);
    }

    /// <summary>Completes order training the way the page does: the practice order was delivered, then Complete.</summary>
    private async Task CompleteTrainingAsync(Guid owner)
    {
        await GuidedSetup().SaveProgressAsync(_tenant, owner, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeOnTheWay), Ct);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false);
        var completed = await Coordinator(Owner).CompleteTrainingAsync(_tenant, owner, Principal(owner), Ct);
        Assert.Equal(GuidedSetupCoordinator.TrainingCompletedMessageKey, completed.SuccessMessageKey);
        Assert.Equal(GuidedSetupStatus.Completed, (await GuidedSetup().GetAsync(_tenant, owner, Ct)).Status);
    }

    private GuidedSetupService GuidedSetup() => new(_tenants, _clock);

    private OrderReadService Orders() => new(_tenants, _clock, NullLogger<OrderReadService>.Instance);

    private GuidedSetupCoordinator Coordinator(TenantNavigationPermissions permissions) =>
        new(GuidedSetup(), new FixedNavigation(permissions), new FakeSetupStatus(), _demos, new FakePrintBridgeDevices(), new CapturingLogger());

    private async Task<LiveScreenSnapshotResult> LiveDataAsync(
        Guid userId, TenantNavigationPermissions permissions, GuidedSetupCoordinator? coordinator = null, Guid? tenantId = null)
    {
        var controller = new OrdersController(
            new FixedTenant(tenantId ?? _tenant),
            Orders(),
            actions: null!,
            orderSyncSettings: null!,
            orderSettings: null!,
            receiptCreation: null!,
            manualPrint: null!,
            demos: _demos,
            authorization: new PoliciesOf(permissions),
            orderSettingsValidator: null!,
            logger: NullLogger<OrdersController>.Instance,
            localizer: new KeyLocalizer())
        {
            ControllerContext = new ControllerContext
            {
                // Request services as in the app, including the tenant-mode reader: isolation must not consult it.
                HttpContext = new DefaultHttpContext
                {
                    User = Principal(userId),
                    RequestServices = new ServiceCollection()
                        .AddSingleton<Wasla.Application.Abstractions.Setup.ITenantOperationalModeService>(new TenantOperationalModeService(_tenants))
                        .BuildServiceProvider()
                }
            }
        };

        var ok = Assert.IsType<OkObjectResult>(await controller.LiveData(coordinator ?? Coordinator(permissions), new TenantOperationalModeService(_tenants), Ct));
        return Assert.IsType<LiveScreenSnapshotResult>(ok.Value);
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Tenant"));

    private GuidedDemoSessionState PracticeOrder() =>
        new(Guid.NewGuid(), _owner, "lokanta", OrderStatus.New, _clock.UtcNow, "Demo.CustomerName", null,
            [new GuidedDemoLine("Demo.Item", 1, 100m)]);

    private async Task<Guid> Seed(
        string code, OrderStatus status, DateTime receivedAt, DateTime? deliveredAt = null, string customer = "Test Customer", Guid? tenantId = null)
    {
        var id = Guid.NewGuid();
        await using var db = await _tenants.CreateAsync(tenantId ?? _tenant, CancellationToken.None);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = code,
            InternalStatus = status,
            PlatformStatus = "Created",
            CustomerName = customer,
            CustomerPhone = "+905550000000",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = receivedAt,
            ReceivedAt = receivedAt,
            DeliveredAt = deliveredAt,
            RawPayloadJson = "{}",
            CreatedAt = receivedAt,
            UpdatedAt = receivedAt,
            Items =
            [
                new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 100m, TotalPrice = 100m, CreatedAt = receivedAt, UpdatedAt = receivedAt }
            ]
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class FixedTenant(Guid tenantId) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } = new(tenantId, "Tenant", "tenant", "tenant.wasla.local");
    }

    /// <summary>The two policies LiveData asks about, answered from the role's navigation permissions.</summary>
    private sealed class PoliciesOf(TenantNavigationPermissions permissions) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName switch
            {
                TenantPolicies.TenantOwner when permissions.CanUseGuidedSetup => AuthorizationResult.Success(),
                TenantPolicies.CanManageOrders when permissions.CanManageOrders => AuthorizationResult.Success(),
                _ => AuthorizationResult.Failed()
            });

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            throw new NotSupportedException();
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
