using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 1 of the Orders / Live Screen separation: navigation, wording, and view-mode preference isolation.
/// </summary>
public sealed class OrdersLiveScreenNavigationTests
{
    private static readonly string[] LocalizedCultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public void LiveDisplayRoute_RemainsAvailableUnderLiveScreenPolicy()
    {
        var source = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("[HttpGet(\"live-display\")]", source, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = TenantPolicies.CanViewLiveScreen)]", source, StringComparison.Ordinal);
        Assert.Contains("return View(\"LiveDisplay\", vm);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantSidebar_ContainsLocalizedLiveScreenItemGuardedByLiveScreenPermission()
    {
        var source = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");

        Assert.Contains("navPermissions.CanViewLiveScreen", source, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"LiveDisplay\"", source, StringComparison.Ordinal);
        Assert.Contains("@L[\"Layout.LiveScreen\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Canlı Ekran<", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Live Screen<", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Kitchen, true)]
    [InlineData(UserRole.Cashier, true)]
    [InlineData(UserRole.Viewer, true)]
    public async Task NavigationPermissions_ExposeLiveScreenThroughCanViewLiveScreenPolicy(UserRole role, bool expected)
    {
        var authorization = BuildAuthorizationService(_tenantId);
        var navigation = new TenantNavigationAuthorizationService(authorization);

        var permissions = await navigation.GetPermissionsAsync(Principal(_tenantId, role));
        var policyResult = await authorization.AuthorizeAsync(
            Principal(_tenantId, role),
            resource: null,
            TenantPolicies.CanViewLiveScreen);

        Assert.Equal(expected, permissions.CanViewLiveScreen);
        Assert.Equal(policyResult.Succeeded, permissions.CanViewLiveScreen);
    }

    [Fact]
    public async Task NavigationPermissions_DoNotExposeLiveScreenForAnotherTenant()
    {
        var authorization = BuildAuthorizationService(_tenantId);
        var navigation = new TenantNavigationAuthorizationService(authorization);

        var permissions = await navigation.GetPermissionsAsync(Principal(Guid.NewGuid(), UserRole.Owner));

        Assert.False(permissions.CanViewLiveScreen);
    }

    [Fact]
    public void LiveScreenLabels_AreTranslatedInEverySupportedCulture()
    {
        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);

            Assert.True(resources.TryGetValue("Layout.LiveScreen", out var navLabel), $"Layout.LiveScreen missing for {culture}.");
            Assert.False(string.IsNullOrWhiteSpace(navLabel), $"Layout.LiveScreen empty for {culture}.");

            Assert.True(resources.TryGetValue("Orders.LiveDisplay.Subtitle", out var liveSubtitle), $"Orders.LiveDisplay.Subtitle missing for {culture}.");
            Assert.False(string.IsNullOrWhiteSpace(liveSubtitle), $"Orders.LiveDisplay.Subtitle empty for {culture}.");

            Assert.True(resources.TryGetValue("Orders.Subtitle", out var ordersSubtitle), $"Orders.Subtitle missing for {culture}.");
            Assert.False(string.IsNullOrWhiteSpace(ordersSubtitle), $"Orders.Subtitle empty for {culture}.");
        }

        Assert.Equal("Canlı Ekran", ReadResourceValues("tr-TR")["Layout.LiveScreen"]);
        Assert.Equal("Live Screen", ReadResourceValues("en-US")["Layout.LiveScreen"]);
    }

    [Fact]
    public void OrdersEntryPoint_UsesLiveScreenWordingInsteadOfFullscreen()
    {
        var ordersView = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.Contains("@L[\"Orders.OpenLiveDisplay\"]", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("@L[\"Orders.Fullscreen\"]", ordersView, StringComparison.Ordinal);

        var english = ReadResourceValues("en-US");
        Assert.Equal("Open Live Screen", english["Orders.OpenLiveDisplay"]);
        Assert.Equal("Live Screen", english["Orders.LiveDisplay.Title"]);
    }

    [Fact]
    public void OrdersAndLiveScreen_UseSeparateViewModePreferenceKeys()
    {
        var ordersView = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var liveView = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var viewModeScript = ReadRepositoryFile("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-view-mode.js");

        Assert.Contains("viewModeStorageKey: \"Wasla.orders.viewMode\"", ordersView, StringComparison.Ordinal);
        Assert.Contains("viewModeStorageKey: \"Wasla.liveScreen.viewMode\"", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("Wasla.liveScreen.viewMode", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("Wasla.orders.viewMode", liveView, StringComparison.Ordinal);

        Assert.Contains("O.opts.viewModeStorageKey", viewModeScript, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.getItem(STORAGE_KEY)", viewModeScript, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.setItem(STORAGE_KEY", viewModeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Orders_DefaultsToTableAndDropsKitchenModeSelector()
    {
        var ordersView = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");

        Assert.Contains("defaultViewMode: \"table\"", ordersView, StringComparison.Ordinal);
        Assert.Contains("data-orders-view-mode=\"table\"", ordersView, StringComparison.Ordinal);
        Assert.Contains("data-orders-view-mode=\"compact\"", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("data-orders-view-mode=\"kitchen\"", ordersView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.Subtitle\"]", ordersView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_UsesDedicatedOperationalCardsWithoutViewModeSelectors()
    {
        var liveView = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("_LiveScreenOrders", liveView, StringComparison.Ordinal);
        Assert.Contains("ordersLiveScreenHost", liveView, StringComparison.Ordinal);
        Assert.Contains("liveScreenUrl: \"/orders/live-screen\"", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.LiveDisplay.Subtitle\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("F11", liveView, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplayClose", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("data-orders-view-mode=", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("_OrdersTable", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModeScript_FallsBackToPageDefaultWhenStoredModeIsUnavailable()
    {
        var viewModeScript = ReadRepositoryFile("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-view-mode.js");

        Assert.Contains("O.opts.defaultViewMode", viewModeScript, StringComparison.Ordinal);
        Assert.Contains("availableModes().indexOf(v) >= 0", viewModeScript, StringComparison.Ordinal);
        Assert.Contains("return defaultMode();", viewModeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFullscreenQuery_StillRedirectsToLiveScreen()
    {
        var source = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("if (IsLegacyFullscreenRequest())", source, StringComparison.Ordinal);
        Assert.Contains("return RedirectToAction(nameof(LiveDisplay));", source, StringComparison.Ordinal);
        Assert.Contains("Request.Query.TryGetValue(\"fullscreen\", out var value)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderManagementActions_KeepManageOrdersPolicy()
    {
        var controller = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var table = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");

        Assert.Contains("[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]", controller, StringComparison.Ordinal);
        Assert.Equal(6, CountOccurrences(controller, "[Authorize(Policy = TenantPolicies.CanManageOrders)]"));
        Assert.Contains("TenantPolicies.CanManageOrders", table, StringComparison.Ordinal);
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
            GetRepositoryRoot(),
            "src",
            "Wasla.Web",
            "Resources",
            $"SharedResource.{culture}.resx");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments) =>
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
            AddPolicy(options, TenantPolicies.TenantOwner, UserRole.Owner);
            AddPolicy(options, TenantPolicies.TenantManagerOrOwner, UserRole.Owner, UserRole.Manager);
            AddPolicy(options, TenantPolicies.CanManageTenantUsers, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManageTenantSettings, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddPolicy(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddPolicy(options, TenantPolicies.CanViewReports, UserRole.Owner, UserRole.Manager, UserRole.Viewer);
        });
        services.AddSingleton<ICurrentTenantService>(new FixedCurrentTenantService(currentTenantId));
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

    private sealed class FixedCurrentTenantService : ICurrentTenantService
    {
        public FixedCurrentTenantService(Guid tenantId)
        {
            CurrentTenant = new ResolvedTenantDto(tenantId, "Tenant", "tenant", "tenant.wasla.local");
        }

        public ResolvedTenantDto? CurrentTenant { get; }
    }
}
