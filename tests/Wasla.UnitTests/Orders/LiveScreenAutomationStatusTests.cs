using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.GuidedSetup;
using Wasla.UnitTests.Setup;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The automation indicators show the effective state, not just the saved settings: while the tenant is in Setup,
/// automatic approval and automatic receipts that are configured on are PendingSetup (they start once guided setup is
/// completed or skipped), order synchronization is unaffected, and the saved settings never change. The server computes
/// it on every Live Screen poll, so Setup → Live shows up on the next snapshot. Real services on SQLite, real policies.
/// </summary>
public sealed class LiveScreenAutomationStatusTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The rule -----------------------------------------------------------------------------------------------------

    [Theory]
    // mode, sync, approve, receipt → sync, approve, receipt
    [InlineData(TenantOperationalMode.Setup, true, false, false, AutomationState.Active, AutomationState.Off, AutomationState.Off)]
    [InlineData(TenantOperationalMode.Setup, false, false, false, AutomationState.Off, AutomationState.Off, AutomationState.Off)]
    [InlineData(TenantOperationalMode.Setup, true, true, false, AutomationState.Active, AutomationState.PendingSetup, AutomationState.Off)]
    [InlineData(TenantOperationalMode.Setup, true, false, true, AutomationState.Active, AutomationState.Off, AutomationState.PendingSetup)]
    [InlineData(TenantOperationalMode.Setup, true, true, true, AutomationState.Active, AutomationState.PendingSetup, AutomationState.PendingSetup)]
    [InlineData(TenantOperationalMode.Setup, false, true, true, AutomationState.Off, AutomationState.PendingSetup, AutomationState.PendingSetup)]
    [InlineData(TenantOperationalMode.Live, true, true, true, AutomationState.Active, AutomationState.Active, AutomationState.Active)]
    [InlineData(TenantOperationalMode.Live, true, false, false, AutomationState.Active, AutomationState.Off, AutomationState.Off)]
    [InlineData(TenantOperationalMode.Live, false, true, false, AutomationState.Off, AutomationState.Active, AutomationState.Off)]
    public void ConfiguredVersusEffective(
        TenantOperationalMode mode, bool sync, bool approve, bool receipt,
        AutomationState expectedSync, AutomationState expectedApprove, AutomationState expectedReceipt)
    {
        var status = new TenantAutomationStatus(mode, sync, approve, receipt);

        Assert.Equal(expectedSync, status.OrderSync);
        Assert.Equal(expectedApprove, status.AutoApprove);
        Assert.Equal(expectedReceipt, status.AutoReceipt);
        // Order synchronization is never "pending": Setup does not stop ingestion.
        Assert.NotEqual(AutomationState.PendingSetup, status.OrderSync);
        // The configured values are kept as they are.
        Assert.Equal((sync, approve, receipt), (status.OrderSyncConfigured, status.AutoApproveConfigured, status.AutoReceiptConfigured));
    }

    // The read ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATenantWithoutASettingsRow_KeepsTheEstablishedLiveDefaults_AndNothingIsWritten()
    {
        var status = await new TenantOperationalModeService(_tenants).GetAutomationStatusAsync(_tenant, Ct);

        Assert.Equal(TenantAutomationStatus.WithoutSettings, status);
        Assert.Equal(TenantOperationalMode.Live, status.Mode);
        Assert.Equal(AutomationState.Active, status.OrderSync);
        Assert.Equal(AutomationState.Off, status.AutoApprove);
        Assert.Equal(AutomationState.Off, status.AutoReceipt);

        var snapshot = await LiveDataAsync(UserRole.Manager);
        Assert.Equal(new LiveScreenAutomationStatus(AutomationState.Active, AutomationState.Off, AutomationState.Off), snapshot.Automation);
        var settings = await OrderSettingsJsonAsync();
        Assert.Equal("Off", settings.GetProperty("effective").GetProperty("autoApprove").GetString());
        Assert.Equal("Off", settings.GetProperty("effective").GetProperty("autoReceipt").GetString());

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(0, await db.TenantOperationalSettings.CountAsync(Ct));
    }

    [Fact]
    public async Task AnotherTenantsModeNeverAffectsThisTenant()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Setup, autoApprove: true, autoPrint: true);
        await _tenants.SeedSettingsAsync(_otherTenant, TenantOperationalMode.Live, autoApprove: true, autoPrint: true);
        var service = new TenantOperationalModeService(_tenants);

        Assert.Equal(AutomationState.PendingSetup, (await service.GetAutomationStatusAsync(_tenant, Ct)).AutoApprove);
        Assert.Equal(AutomationState.Active, (await service.GetAutomationStatusAsync(_otherTenant, Ct)).AutoApprove);

        var mine = await LiveDataAsync(UserRole.Manager, _tenant);
        var theirs = await LiveDataAsync(UserRole.Manager, _otherTenant);
        Assert.Equal(AutomationState.PendingSetup, mine.Automation!.AutoReceipt);
        Assert.Equal(AutomationState.Active, theirs.Automation!.AutoReceipt);
    }

    // The Live Screen ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task InSetup_TheSnapshotShowsSyncActive_AndConfiguredAutomationPending(UserRole role)
    {
        await NewSetupTenantAsync(autoApprove: true, autoPrint: false);

        var snapshot = await LiveDataAsync(role);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Web));
        var automation = json.RootElement.GetProperty("automation");

        Assert.Equal("Active", automation.GetProperty("orderSync").GetString());
        Assert.Equal("PendingSetup", automation.GetProperty("autoApprove").GetString());
        Assert.Equal("Off", automation.GetProperty("autoReceipt").GetString());
        Assert.Equal(["autoApprove", "autoReceipt", "orderSync"], automation.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(GuidedSetupCommand.Skip)]
    [InlineData(GuidedSetupCommand.Complete)]
    public async Task WhenTheOwnerSkipsOrCompletes_TheNextPollShowsActive_WithoutAReload(GuidedSetupCommand command)
    {
        await NewSetupTenantAsync(autoApprove: true, autoPrint: true);
        var guidedSetup = new GuidedSetupService(_tenants, TimeProvider.System);
        await guidedSetup.StartAsync(_tenant, _owner, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.Intro), Ct);
        var before = await LiveDataAsync(UserRole.Manager);
        Assert.Equal(AutomationState.PendingSetup, before.Automation!.AutoApprove);
        Assert.Equal(AutomationState.PendingSetup, before.Automation.AutoReceipt);

        // In another tab: the Owner skips or completes guided setup.
        var result = command == GuidedSetupCommand.Skip
            ? await guidedSetup.SkipAsync(_tenant, _owner, Ct)
            : await guidedSetup.CompleteAsync(_tenant, _owner, Ct);
        Assert.Equal(GuidedSetupOutcome.Applied, result.Outcome);

        var after = await LiveDataAsync(UserRole.Manager);
        Assert.Equal(new LiveScreenAutomationStatus(AutomationState.Active, AutomationState.Active, AutomationState.Active), after.Automation);
        var settings = await OrderSettingsJsonAsync();
        Assert.Equal("Active", settings.GetProperty("effective").GetProperty("autoApprove").GetString());
        Assert.Equal("Active", settings.GetProperty("effective").GetProperty("autoReceipt").GetString());
    }

    [Fact]
    public async Task RenderingAndPolling_NeverChangeTheSavedSettings_AndTheSettingsPageShowsBoth()
    {
        await NewSetupTenantAsync(autoApprove: true, autoPrint: true);
        var before = await SettingsRowAsync();

        for (var i = 0; i < 3; i++)
            await LiveDataAsync(UserRole.Owner);
        var settings = await OrderSettingsJsonAsync();

        Assert.Equal(before, await SettingsRowAsync());
        Assert.True(settings.GetProperty("autoApproveNewOrders").GetBoolean(), "the saved setting is shown as configured");
        Assert.True(settings.GetProperty("autoPrintReceiptOnAutoApprove").GetBoolean());
        Assert.Equal("PendingSetup", settings.GetProperty("effective").GetProperty("autoApprove").GetString());
        Assert.Equal("PendingSetup", settings.GetProperty("effective").GetProperty("autoReceipt").GetString());
    }

    // Roles ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every Live Screen role (the endpoint stays under <c>CanViewLiveScreen</c>) receives the orders and the same
    /// read-only effective automation states on every poll, and nothing else about the settings.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task EveryLiveScreenRole_GetsTheOrders_AndTheReadOnlyAutomationStatus(UserRole role)
    {
        await NewSetupTenantAsync(autoApprove: true, autoPrint: false);
        var order = await SeedOrderAsync();
        var modes = new FakeTenantModes { Mode = TenantOperationalMode.Setup };

        Assert.True(await AllowedAsync(role, typeof(OrdersController), nameof(OrdersController.LiveData)));

        var first = await LiveDataAsync(role);
        var second = await LiveDataAsync(role, modes: modes);

        Assert.Contains(first.Orders, o => o.Id == order);
        Assert.Contains(second.Orders, o => o.Id == order);
        Assert.Equal(new LiveScreenAutomationStatus(AutomationState.Active, AutomationState.PendingSetup, AutomationState.Off), first.Automation);
        Assert.NotNull(second.Automation);
        Assert.Equal([_tenant], modes.ReadTenants);

        // Only the three effective states travel: no saved flag, mode, copy count or any other configuration value.
        var json = JsonSerializer.Serialize(first, Web);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["autoApprove", "autoReceipt", "orderSync"],
            document.RootElement.GetProperty("automation").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var forbidden in new[]
                 {
                     "autoApproveNewOrders", "autoPrintReceiptOnAutoApprove", "orderSyncEnabled", "receiptPrintCopyCount",
                     "operationalMode", "Configured", "token", "secret", "password", "connection"
                 })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllFiveRoles_SeeTheSameStatus_ForTheSameTenantState()
    {
        await NewSetupTenantAsync(autoApprove: false, autoPrint: true);
        var roles = new[] { UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer };

        var statuses = new List<LiveScreenAutomationStatus?>();
        foreach (var role in roles)
            statuses.Add((await LiveDataAsync(role)).Automation);

        Assert.All(statuses, status =>
            Assert.Equal(new LiveScreenAutomationStatus(AutomationState.Active, AutomationState.Off, AutomationState.PendingSetup), status));
    }

    /// <summary>
    /// The settings stay protected: reading or changing order synchronization, order automation or the receipt printer
    /// settings needs Manager/Owner (receipt printer: Owner), exactly as before. Evaluated with the policies Program.cs
    /// registers, combining the controller's and the action's [Authorize] like MVC does.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Owner, true, true)]
    [InlineData(UserRole.Manager, true, false)]
    [InlineData(UserRole.Kitchen, false, false)]
    [InlineData(UserRole.Cashier, false, false)]
    [InlineData(UserRole.Viewer, false, false)]
    public async Task OnlyManagersAndOwners_MayOpenOrChangeTheSettings(UserRole role, bool orderSettings, bool receiptPrinterSettings)
    {
        foreach (var action in new[]
                 {
                     nameof(OrdersController.GetOrderSyncSettings), nameof(OrdersController.UpdateOrderSyncSettings),
                     nameof(OrdersController.GetOrderSettings), nameof(OrdersController.UpdateOrderSettings)
                 })
            Assert.Equal(orderSettings, await AllowedAsync(role, typeof(OrdersController), action));
        Assert.Equal(orderSettings, await AllowedAsync(role, typeof(OrderSettingsController), nameof(OrderSettingsController.Index)));
        foreach (var action in new[]
                 {
                     nameof(ReceiptPrinterSettingsController.Index), nameof(ReceiptPrinterSettingsController.GetTemplateSettings),
                     nameof(ReceiptPrinterSettingsController.UpdateTemplateSettings)
                 })
            Assert.Equal(receiptPrinterSettings, await AllowedAsync(role, typeof(ReceiptPrinterSettingsController), action));

        // The Live Screen itself, with its read-only status, stays open to every Live Screen role.
        Assert.True(await AllowedAsync(role, typeof(OrdersController), nameof(OrdersController.LiveData)));
        Assert.True(await AllowedAsync(role, typeof(OrdersController), nameof(OrdersController.LiveDisplay)));
    }

    [Fact]
    public void TheLiveScreenKeepsItsPolicy_AndTheSettingsKeepTheirs()
    {
        Assert.Equal([TenantPolicies.CanViewOrders, TenantPolicies.CanViewLiveScreen], PoliciesOf(typeof(OrdersController), nameof(OrdersController.LiveData)));
        foreach (var action in new[]
                 {
                     nameof(OrdersController.GetOrderSyncSettings), nameof(OrdersController.UpdateOrderSyncSettings),
                     nameof(OrdersController.GetOrderSettings), nameof(OrdersController.UpdateOrderSettings)
                 })
            Assert.Equal([TenantPolicies.CanViewOrders, TenantPolicies.TenantManagerOrOwner], PoliciesOf(typeof(OrdersController), action));
    }

    /// <summary>The controller in these tests uses the shared policy mirror; it must agree with Program.cs.</summary>
    [Fact]
    public async Task TheSharedTestPolicies_MatchProgramCs()
    {
        foreach (var policy in new[]
                 {
                     TenantPolicies.TenantOwner, TenantPolicies.TenantManagerOrOwner, TenantPolicies.CanViewOrders,
                     TenantPolicies.CanViewLiveScreen, TenantPolicies.CanManageTenantSettings
                 })
            foreach (var role in Enum.GetValues<UserRole>())
                Assert.True(
                    (await GuidedSetupOwnerOnlyTests.Authorization(_tenant).AuthorizeAsync(Principal(role, _tenant), policy)).Succeeded
                        == (await RealPolicies(_tenant).AuthorizeAsync(Principal(role, _tenant), policy)).Succeeded,
                    $"{policy} / {role}");
    }

    // Failures and isolation ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    public async Task AFailedStatusRead_KeepsTheOrders_AndLeavesTheIndicatorsAsTheyAre(UserRole role)
    {
        await NewSetupTenantAsync(autoApprove: true, autoPrint: true);
        var order = await SeedOrderAsync();

        var snapshot = await LiveDataAsync(role, modes: new FakeTenantModes { FailReads = true });

        Assert.Null(snapshot.Automation);
        Assert.Contains(snapshot.Orders, o => o.Id == order);
    }

    [Fact]
    public async Task TheStatusIsReadForTheResolvedTenant_NeverFromTheRequest()
    {
        await NewSetupTenantAsync(autoApprove: true, autoPrint: true);
        var modes = new FakeTenantModes();

        await LiveDataAsync(UserRole.Kitchen, modes: modes);

        Assert.Equal([_tenant], modes.ReadTenants);
        Assert.DoesNotContain(typeof(OrdersController).GetMethod(nameof(OrdersController.LiveData))!.GetParameters(),
            parameter => parameter.ParameterType == typeof(Guid) || parameter.ParameterType == typeof(Guid?) || parameter.ParameterType == typeof(string));
    }

    public void Dispose() => _tenants.Dispose();

    private async Task NewSetupTenantAsync(bool autoApprove, bool autoPrint)
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Setup, autoApprove, autoPrint);
        await _tenants.SeedUserAsync(_tenant, _owner);
    }

    private async Task<(TenantOperationalMode, bool, bool, bool, int, DateTime)> SettingsRowAsync()
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var row = await db.TenantOperationalSettings.AsNoTracking().SingleAsync(Ct);
        return (row.OperationalMode, row.OrderSyncEnabled, row.AutoApproveNewOrders, row.AutoPrintReceiptOnAutoApprove, row.ReceiptPrintCopyCount, row.UpdatedAt);
    }

    private async Task<LiveScreenSnapshotResult> LiveDataAsync(UserRole role, Guid? tenantId = null, ITenantOperationalModeService? modes = null)
    {
        var tenant = tenantId ?? _tenant;
        var coordinator = new GuidedSetupCoordinator(new GuidedSetupService(_tenants, TimeProvider.System),
            new TenantNavigationAuthorizationService(RealPolicies(tenant)), new FakeSetupStatus(), new FakeDemos(),
            new FakePrintBridgeDevices(), new CapturingLogger());
        var ok = Assert.IsType<OkObjectResult>(
            await Controller(role, tenant).LiveData(coordinator, modes ?? new TenantOperationalModeService(_tenants), Ct));
        return Assert.IsType<LiveScreenSnapshotResult>(ok.Value);
    }

    private async Task<JsonElement> OrderSettingsJsonAsync()
    {
        var ok = Assert.IsType<OkObjectResult>(await Controller(UserRole.Owner, _tenant).GetOrderSettings(Ct));
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, Web)).RootElement.Clone();
    }

    private OrdersController Controller(UserRole role, Guid tenantId) =>
        new(
            new FixedTenant(tenantId),
            new OrderReadService(_tenants, TimeProvider.System, NullLogger<OrderReadService>.Instance),
            actions: null!,
            orderSyncSettings: null!,
            orderSettings: new TenantOrderSettingsService(_tenants, null!, NullLogger<TenantOrderSettingsService>.Instance),
            receiptCreation: null!,
            manualPrint: null!,
            demos: new FakeDemos(),
            authorization: RealPolicies(tenantId),
            orderSettingsValidator: null!,
            logger: NullLogger<OrdersController>.Instance,
            localizer: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(role, tenantId) }
            }
        };

    /// <summary>
    /// Whether the role may call the action: the controller's and the action's [Authorize] combined as the middleware
    /// does, evaluated with the policies exactly as Program.cs registers them.
    /// </summary>
    private async Task<bool> AllowedAsync(UserRole role, Type controller, string action)
    {
        var policy = await GuidedSetupOwnerOnlyTests.EndpointPolicyAsync(controller, action);
        return (await GuidedSetupOwnerOnlyTests.Authorization(_tenant).AuthorizeAsync(Principal(role, _tenant), null, policy)).Succeeded;
    }

    /// <summary>The policy names MVC combines for an action: the controller's [Authorize] and the action's.</summary>
    private static string[] PoliciesOf(Type controller, string action) =>
        controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(controller.GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(attribute => attribute.Policy)
            .OfType<string>()
            .ToArray();

    private ClaimsPrincipal Principal(UserRole role, Guid tenantId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _owner.ToString()),
                new Claim("TenantId", tenantId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            authenticationType: "Tenant"));

    private async Task<Guid> SeedOrderAsync()
    {
        var id = Guid.NewGuid();
        var receivedAt = DateTime.UtcNow.AddMinutes(-5);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = "automation-roles",
            ExternalOrderCode = "automation-roles",
            IdempotencyKey = "automation-roles",
            InternalStatus = OrderStatus.New,
            PlatformStatus = "Created",
            CustomerName = "Test Customer",
            CustomerPhone = "+905550000000",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = receivedAt,
            ReceivedAt = receivedAt,
            RawPayloadJson = "{}",
            CreatedAt = receivedAt,
            UpdatedAt = receivedAt,
            Items =
            [
                new OrderItem { ProductName = "Lahmacun", Quantity = 1, UnitPrice = 100m, TotalPrice = 100m, CreatedAt = receivedAt, UpdatedAt = receivedAt }
            ]
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }
}
