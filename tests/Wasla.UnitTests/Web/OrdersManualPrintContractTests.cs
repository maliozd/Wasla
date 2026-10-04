using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 2B5: manual printing endpoint contract, authorization, UI placement, and localization.
/// </summary>
public sealed class OrdersManualPrintContractTests
{
    private static readonly string[] LocalizedCultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public void PrintEndpoint_IsPostOnlyWithAntiforgeryAndPrintingPolicy()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        var index = controller.IndexOf("public async Task<IActionResult> Print(", StringComparison.Ordinal);
        Assert.True(index > 0, "Manual print action was not found.");

        var attributes = controller[..index];
        var lastAttributeBlock = attributes[^220..];
        Assert.Contains("[HttpPost(\"{id:guid}/print\")]", lastAttributeBlock, StringComparison.Ordinal);
        Assert.Contains("[ValidateAntiForgeryToken]", lastAttributeBlock, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = TenantPolicies.CanManualPrint)]", lastAttributeBlock, StringComparison.Ordinal);

        // No HttpGet variant may perform the state change.
        Assert.DoesNotContain("[HttpGet(\"{id:guid}/print\")]", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintEndpoint_DelegatesToTheSharedServiceAndPassesTenantScope()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("_manualPrint.QueueReceiptPrintAsync(tenant.Id, id, tenant.Name, ct)", controller, StringComparison.Ordinal);
        Assert.Contains("ManualOrderPrintOutcome.OrderNotFound => NotFound(body)", controller, StringComparison.Ordinal);
        Assert.Contains("ManualOrderPrintOutcome.AlreadyQueued => Conflict(body)", controller, StringComparison.Ordinal);

        var printIndex = controller.IndexOf("public async Task<IActionResult> Print(", StringComparison.Ordinal);
        var printAction = controller[printIndex..];
        var nextAction = printAction.IndexOf("[HttpPost(\"{id:guid}/approve\")]", StringComparison.Ordinal);
        var printBody = nextAction > 0 ? printAction[..nextAction] : printAction;

        Assert.Contains("success = result.Success", printBody, StringComparison.Ordinal);
        Assert.Contains("message = _localizer[result.MessageKey].Value", printBody, StringComparison.Ordinal);
        Assert.DoesNotContain("printJobId", printBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PrintJobId", printBody, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome =", printBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, true)]
    [InlineData(UserRole.Kitchen, false)]
    [InlineData(UserRole.Viewer, false)]
    public async Task ManualPrintPolicy_AllowsOnlyPrintingRoles(UserRole role, bool expected)
    {
        var authorization = BuildAuthorizationService(_tenantId);

        var result = await authorization.AuthorizeAsync(
            Principal(_tenantId, role),
            resource: null,
            TenantPolicies.CanManualPrint);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task ManualPrintPolicy_DeniesUsersFromAnotherTenant()
    {
        var authorization = BuildAuthorizationService(_tenantId);

        var result = await authorization.AuthorizeAsync(
            Principal(Guid.NewGuid(), UserRole.Owner),
            resource: null,
            TenantPolicies.CanManualPrint);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PrintActionPartial_IsGatedByTheManualPrintPolicy()
    {
        var partial = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderPrintAction.cshtml");

        Assert.Contains("TenantPolicies.CanManualPrint", partial, StringComparison.Ordinal);
        Assert.Contains("@if (canManualPrint)", partial, StringComparison.Ordinal);
        Assert.Contains("data-order-print=\"@Model.OrderId\"", partial, StringComparison.Ordinal);
        Assert.Contains("bi-printer", partial, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveModalAndFullDetailsPage_ShareTheSamePrintPartial()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderDetailPanel.cshtml");
        var details = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Details.cshtml");

        Assert.Contains("_OrderPrintAction", panel, StringComparison.Ordinal);
        Assert.Contains("_OrderPrintAction", details, StringComparison.Ordinal);

        // Lifecycle stays the primary action in the modal footer.
        Assert.True(
            panel.IndexOf("_OrderLifecycleActions", StringComparison.Ordinal)
            < panel.IndexOf("_OrderPrintAction", StringComparison.Ordinal),
            "Print action must sit after the lifecycle action in the modal footer.");
    }

    [Fact]
    public void ModalDetailLink_UsesOpenFullOrderWording()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderDetailPanel.cshtml");

        Assert.Contains("@L[\"Orders.Details.OpenFullOrder\"]", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Orders.History.ViewDetails", panel, StringComparison.Ordinal);
        Assert.Contains("href=\"/orders/details/@Model.Id\"", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintAction_IsNotAddedToEveryLiveCardOrOrdersRow()
    {
        var cards = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");

        Assert.DoesNotContain("_OrderPrintAction", cards, StringComparison.Ordinal);
        Assert.DoesNotContain("data-order-print", cards, StringComparison.Ordinal);
        Assert.DoesNotContain("_OrderPrintAction", table, StringComparison.Ordinal);
        Assert.DoesNotContain("data-order-print", table, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintClient_UsesTheServerPipelineOnly()
    {
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-print.js");

        Assert.Contains("method: \"POST\"", js, StringComparison.Ordinal);
        Assert.Contains("RequestVerificationToken", js, StringComparison.Ordinal);

        // No browser printing, no direct desktop-agent calls, no realtime transport.
        Assert.DoesNotContain("window.print", js, StringComparison.Ordinal);
        Assert.DoesNotContain("global.print(", js, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", js, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-printbridge", js, StringComparison.Ordinal);
        Assert.DoesNotContain("signalR", js, StringComparison.Ordinal);
        Assert.DoesNotContain("WebSocket", js, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintClient_RegistersOneDelegatedListenerThatSurvivesRerenders()
    {
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-print.js");

        // Exactly two registrations: the DOMContentLoaded bootstrap and the single delegated click.
        Assert.Equal(2, CountOccurrences(js, "addEventListener("));
        Assert.Contains("document.addEventListener(\"click\"", js, StringComparison.Ordinal);
        Assert.Contains("closest(\"[data-order-print]\")", js, StringComparison.Ordinal);

        // Buttons are re-rendered by the modal and by polling, so no per-button binding is allowed.
        Assert.DoesNotContain("querySelectorAll(\"[data-order-print]\")", js, StringComparison.Ordinal);
        Assert.DoesNotContain("btn.addEventListener", js, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintClient_DisablesDuringFlightAndReflectsServerState()
    {
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-print.js");

        Assert.Contains("inFlight[orderId]", js, StringComparison.Ordinal);
        Assert.Contains("btn.disabled = true;", js, StringComparison.Ordinal);
        Assert.Contains("resp.status === 409", js, StringComparison.Ordinal);
        Assert.Contains("applyQueuedState(btn)", js, StringComparison.Ordinal);
        Assert.Contains("restore(btn, previousMode)", js, StringComparison.Ordinal);
        Assert.Contains("data-confirm-reprint", js, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticReceiptCreation_IsUnchangedByManualPrinting()
    {
        var automatic = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var manual = Read("src", "Wasla.Infrastructure", "Services", "ManualOrderPrintService.cs");
        var autoService = Read("src", "Wasla.Infrastructure", "Services", "OrderReceiptCreationService.cs");

        // Approval still runs the automatic path, and the manual path never calls it.
        Assert.Contains("_receiptCreation.TryCreateOnOrderAcceptedAsync(tenant.Id, id, ct)", automatic, StringComparison.Ordinal);
        Assert.DoesNotContain("IOrderReceiptCreationService", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCreateOnOrderAcceptedAsync", manual, StringComparison.Ordinal);
        Assert.Contains("AutoPrintReceiptOnAutoApprove", autoService, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualPrintService_ReusesTheExistingCreationAndReprintServices()
    {
        var manual = Read("src", "Wasla.Infrastructure", "Services", "ManualOrderPrintService.cs");

        Assert.Contains("_receiptJobs", manual, StringComparison.Ordinal);
        Assert.Contains("TryCreateReceiptJobAsync", manual, StringComparison.Ordinal);
        Assert.Contains("_printJobHistory", manual, StringComparison.Ordinal);
        Assert.Contains("CreateReprintAsync", manual, StringComparison.Ordinal);

        // No second receipt renderer and no direct PrintJob insert.
        Assert.DoesNotContain("ReceiptPayloadBuilder", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("new PrintJob", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("db.PrintJobs.Add", manual, StringComparison.Ordinal);
    }

    [Fact]
    public void ReprintPath_KeepsTheSerializableDuplicateGuard()
    {
        var history = Read("src", "Wasla.Infrastructure", "Services", "PrintJobHistoryService.cs");
        var creation = Read("src", "Wasla.Infrastructure", "Services", "ReceiptPrintJobService.cs");

        foreach (var source in new[] { history, creation })
        {
            Assert.Contains("IsolationLevel.Serializable", source, StringComparison.Ordinal);
            Assert.Contains("PrintJobStatus.Pending || ", source, StringComparison.Ordinal);
        }

        Assert.Contains("PrintBridge.ReprintNotAllowed", history, StringComparison.Ordinal);
        Assert.Contains("PrintJobStatus.Printed or PrintJobStatus.Failed", history, StringComparison.Ordinal);
        Assert.Contains("await tx.RollbackAsync(ct)", history, StringComparison.Ordinal);
        Assert.Contains("await tx.CommitAsync(ct)", history, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMigrationWasAddedForManualPrinting()
    {
        var migrationsRoot = Path.Combine(
            GetRepositoryRoot(), "src", "Wasla.Infrastructure", "Persistence");

        var recent = Directory
            .EnumerateFiles(migrationsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => p.Contains("Migrations", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(name => name is not null
                           && (name.Contains("ManualPrint", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("PrintOrigin", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(recent);
    }

    [Fact]
    public void ManualPrintText_IsLocalizedInEveryCulture()
    {
        string[] keys =
        [
            "Orders.Print.Action",
            "Orders.Print.InQueue",
            "Orders.Print.Hint",
            "Orders.Print.Queued",
            "Orders.Print.ReprintQueued",
            "Orders.Print.AlreadyQueued",
            "Orders.Print.ConfirmReprint",
            "Orders.Print.OrderNotFound",
            "Orders.Print.Failed",
            "Orders.Details.OpenFullOrder",
            "PrintBridge.Reprint"
        ];

        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);
            foreach (var key in keys)
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{key} missing for {culture}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} empty for {culture}.");
            }
        }
    }

    [Fact]
    public void QueuedWording_DoesNotClaimThePaperWasPrinted()
    {
        var english = ReadResourceValues("en-US");

        Assert.Contains("queue", english["Orders.Print.Queued"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queue", english["Orders.Print.ReprintQueued"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has been printed", english["Orders.Print.Queued"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has been printed", english["Orders.Print.ReprintQueued"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrintSurfaces_DoNotHardCodeUserFacingText()
    {
        var partial = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderPrintAction.cshtml");
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-print.js");

        Assert.Contains("L[\"Orders.Print.Action\"]", partial, StringComparison.Ordinal);
        Assert.Contains("L[\"PrintBridge.Reprint\"]", partial, StringComparison.Ordinal);
        Assert.Contains("L[\"Orders.Print.InQueue\"]", partial, StringComparison.Ordinal);
        Assert.DoesNotContain(">Print<", partial, StringComparison.Ordinal);
        Assert.DoesNotContain(">Reprint<", partial, StringComparison.Ordinal);

        // Client strings come from the localized config object or data-* attributes only.
        Assert.DoesNotContain("\"Print\"", js, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Reprint\"", js, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static Dictionary<string, string> ReadResourceValues(string culture)
    {
        var path = Path.Combine(
            GetRepositoryRoot(), "src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot() }.Concat(segments).ToArray()));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static IAuthorizationService BuildAuthorizationService(Guid currentTenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            AddPolicy(options, TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddPolicy(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
        });
        services.AddSingleton<ICurrentTenantService>(new FixedTenant(currentTenantId));
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static void AddPolicy(AuthorizationOptions options, string name, params UserRole[] roles)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new TenantRoleRequirement(roles));
        });
    }

    private static ClaimsPrincipal Principal(Guid tenantId, UserRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim("TenantId", tenantId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            authenticationType: "Tenant"));

    private sealed class FixedTenant : ICurrentTenantService
    {
        public FixedTenant(Guid tenantId)
        {
            CurrentTenant = new ResolvedTenantDto(tenantId, "Tenant", "tenant", "tenant.wasla.local");
        }

        public ResolvedTenantDto? CurrentTenant { get; }
    }
}
