using System.Xml.Linq;

namespace Wasla.UnitTests.Web;

public sealed class WaslaVisualFoundationTests
{
    private static readonly string[] LocalizedCultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    [Fact]
    public void TenantLayout_LoadsFoundationStylesheetOnceAfterThemeAndSite()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");

        Assert.Equal(1, CountOccurrences(layout, "css/wasla-foundation.css"));
        Assert.True(
            layout.IndexOf("css/wasla-theme.css", StringComparison.Ordinal)
            < layout.IndexOf("css/site.css", StringComparison.Ordinal));
        Assert.True(
            layout.IndexOf("css/site.css", StringComparison.Ordinal)
            < layout.IndexOf("css/wasla-foundation.css", StringComparison.Ordinal));
        Assert.True(
            layout.IndexOf("css/wasla-foundation.css", StringComparison.Ordinal)
            < layout.IndexOf("css/wasla-toast.css", StringComparison.Ordinal));
    }

    [Fact]
    public void TenantLayout_DoesNotAddExternalFontOrDesignSystemDependencies()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.DoesNotContain("fonts.googleapis.com", layout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.gstatic.com", layout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.googleapis.com", foundation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tailwind", foundation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.jsdelivr.net/npm/@", foundation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TenantSidebar_KeepsAuthorizationRoutesAndLiveScreenContract()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var settings = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantSettingsNav.cshtml");

        Assert.Contains("navPermissions.CanViewReports", layout, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanViewOrders", layout, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanViewLiveScreen", layout, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"LiveDisplay\"", layout, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", layout, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", layout, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManageTenantUsers", layout, StringComparison.Ordinal);
        Assert.Contains("Navigation.UserSettings", layout, StringComparison.Ordinal);
        Assert.Contains("bi bi-people", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("navPermissions.CanManageTenantUsers", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Navigation.UserSettings", settings, StringComparison.Ordinal);
        Assert.Contains("data-bs-toggle=\"dropdown\"", settings, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"Logout\"", settings, StringComparison.Ordinal);
        Assert.Contains("wasla-tenant-shell", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void UsersIndex_KeepsAddUserAndDetailsContracts()
    {
        var index = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Index.cshtml");
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "TenantUsersController.cs");

        Assert.Contains("[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageTenantUsers)]", controller, StringComparison.Ordinal);
        Assert.Contains("ListUsersAsync", controller, StringComparison.Ordinal);
        Assert.Contains("TenantUserRoleOptions.All", controller, StringComparison.Ordinal);
        Assert.Contains("ActionUrl = \"/settings/users/create\"", index, StringComparison.Ordinal);
        Assert.Contains("Eyebrow = L[\"Navigation.Settings\"].Value", index, StringComparison.Ordinal);
        Assert.DoesNotContain("Eyebrow = L[\"Navigation.UserSettings\"]", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.AddUser", index, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"Details\"", index, StringComparison.Ordinal);
        Assert.Contains("value=\"@role.ToString()\"", index, StringComparison.Ordinal);
        Assert.Contains("data-role=\"@user.Role.ToString()\"", index, StringComparison.Ordinal);
        Assert.Contains("wasla-col--secondary", index, StringComparison.Ordinal);
        Assert.Contains("wasla-table__cell-label", index, StringComparison.Ordinal);
        Assert.Contains("data-label=\"@L[\"TenantUsers.Role\"]\"", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.Role.{user.Role}", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.Active", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.Inactive", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.LastLoginNotRecorded", index, StringComparison.Ordinal);
        Assert.Contains("data-users-filter", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.EmptyTitle", index, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.NoResultsTitle", index, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-action=\"ChangeRole\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain(">Add user<", index, StringComparison.Ordinal);
        Assert.DoesNotContain(">Kullanıcı ekle<", index, StringComparison.Ordinal);
    }

    [Fact]
    public void UsersFilterScript_FiltersExistingRowsWithoutDuplicatingTheList()
    {
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "tenant-users-index.js");
        var index = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Index.cshtml");

        Assert.Contains("data-user-row", js, StringComparison.Ordinal);
        Assert.Contains("row.hidden", js, StringComparison.Ordinal);
        Assert.Contains("replace(/İ/g, \"i\")", js, StringComparison.Ordinal);
        Assert.Contains("tabindex", js, StringComparison.Ordinal);
        Assert.DoesNotContain("cloneNode", js, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", js, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(index, "data-user-row"));
        Assert.Equal(1, CountOccurrences(index, "class=\"wasla-table\""));
        Assert.DoesNotContain("wasla-table wasla-table--mobile", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase2C_DoesNotModifyOrdersOrLiveScreenFeatureFiles()
    {
        var liveScreen = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");
        var detail = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderDetailPanel.cshtml");

        Assert.Contains("wasla-live-screen-card", liveScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("oh-live-screen-card", liveScreen, StringComparison.Ordinal);
        Assert.Contains("wasla-orders-table", table, StringComparison.Ordinal);
        Assert.DoesNotContain("oh-orders-table", table, StringComparison.Ordinal);
        Assert.Contains("wasla-live-detail", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("oh-live-detail", detail, StringComparison.Ordinal);
        Assert.False(IsDirty("docs/orders-printjob-status-signalr.md"));
        Assert.False(IsDirty("docs/temporary-receipt-test-output.md"));
    }

    [Fact]
    public void LiveScreenLayout_DoesNotLoadTenantFoundationStylesheet()
    {
        var liveLayout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_OrdersDisplayLayout.cshtml");
        var tenantLayout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");

        Assert.DoesNotContain("css/wasla-foundation.css", liveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-tenant-shell", liveLayout, StringComparison.Ordinal);
        Assert.Contains("wasla-tenant-shell", tenantLayout, StringComparison.Ordinal);
        Assert.Contains("body.wasla-tenant-shell", foundation, StringComparison.Ordinal);
        Assert.Contains("--wasla-content-wide: 84rem", foundation, StringComparison.Ordinal);
        Assert.Contains(".wasla-col--secondary", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain(":root {", foundation, StringComparison.Ordinal);
        Assert.Contains("--color-bg: #fafafa;", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("--color-bg: #f6f1ea;", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleLabels_AreLocalizedWhileMachineValuesStayStable()
    {
        var expected = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["en-US"] = new()
            {
                ["TenantUsers.Role.Owner"] = "Owner",
                ["TenantUsers.Role.Manager"] = "Manager",
                ["TenantUsers.Role.Kitchen"] = "Kitchen",
                ["TenantUsers.Role.Cashier"] = "Cashier",
                ["TenantUsers.Role.Viewer"] = "Viewer"
            },
            ["tr-TR"] = new()
            {
                ["TenantUsers.Role.Owner"] = "Sahip",
                ["TenantUsers.Role.Manager"] = "Yönetici",
                ["TenantUsers.Role.Kitchen"] = "Mutfak",
                ["TenantUsers.Role.Cashier"] = "Kasiyer",
                ["TenantUsers.Role.Viewer"] = "Görüntüleyici"
            },
            ["ar-SA"] = new()
            {
                ["TenantUsers.Role.Owner"] = "المالك",
                ["TenantUsers.Role.Manager"] = "المدير",
                ["TenantUsers.Role.Kitchen"] = "المطبخ",
                ["TenantUsers.Role.Cashier"] = "الكاشير",
                ["TenantUsers.Role.Viewer"] = "العارض"
            },
            ["ru-RU"] = new()
            {
                ["TenantUsers.Role.Owner"] = "Владелец",
                ["TenantUsers.Role.Manager"] = "Менеджер",
                ["TenantUsers.Role.Kitchen"] = "Кухня",
                ["TenantUsers.Role.Cashier"] = "Кассир",
                ["TenantUsers.Role.Viewer"] = "Наблюдатель"
            }
        };

        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);
            foreach (var (key, value) in expected[culture])
            {
                Assert.True(resources.TryGetValue(key, out var actual), $"{key} missing for {culture}.");
                Assert.Equal(value, actual);
            }
        }

        var index = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Index.cshtml");
        Assert.DoesNotContain("value=\"@L[$\"TenantUsers.Role.{role}\"]\"", index, StringComparison.Ordinal);
        Assert.Contains("value=\"@role.ToString()\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase2C_DoesNotIntroduceSignalROrMigrations()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");
        var index = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Index.cshtml");
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "tenant-users-index.js");

        Assert.DoesNotContain("SignalR", foundation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SignalR", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SignalR", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HubConnection", js, StringComparison.OrdinalIgnoreCase);

        var migrationsRoot = Path.Combine(GetRepositoryRoot(), "src", "Wasla.Infrastructure", "Persistence");
        var recent = Directory
            .EnumerateFiles(migrationsRoot, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.Contains("VisualFoundation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(recent);
    }

    [Fact]
    public void TenantShell_HasSingleOffCanvasSidebarAndAccessibleTrigger()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "sidebar-toggle.js");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Equal(1, CountOccurrences(layout, "id=\"app-sidebar-tenant\""));
        Assert.Equal(1, CountOccurrences(layout, "class=\"app-sidebar\""));
        Assert.DoesNotContain("app-sidebar-mobile", layout, StringComparison.Ordinal);
        Assert.Contains("data-wasla-nav=\"trigger\"", layout, StringComparison.Ordinal);
        Assert.Contains("data-wasla-nav=\"backdrop\"", layout, StringComparison.Ordinal);
        Assert.Contains("data-wasla-nav=\"close\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"app-sidebar-tenant\"", layout, StringComparison.Ordinal);
        Assert.Contains("Layout.OpenNavigation", layout, StringComparison.Ordinal);
        Assert.Contains("Layout.CloseNavigation", layout, StringComparison.Ordinal);
        Assert.Contains("Layout.TenantNavigation", layout, StringComparison.Ordinal);
        Assert.Contains("max-width: 991.98px", js, StringComparison.Ordinal);
        Assert.Contains("wasla-nav-open", js, StringComparison.Ordinal);
        Assert.Contains("wasla-nav-lock", js, StringComparison.Ordinal);
        Assert.Contains("e.key === \"Escape\"", js, StringComparison.Ordinal);
        Assert.Contains("openBtn.focus()", js, StringComparison.Ordinal);
        Assert.Contains("moveFocusToTrigger", js, StringComparison.Ordinal);
        Assert.Contains("hideSidebarAfterFocusMoved", js, StringComparison.Ordinal);
        Assert.Contains("isFocusInside(sidebar)", js, StringComparison.Ordinal);
        Assert.DoesNotContain("syncChrome(false)", js, StringComparison.Ordinal);
        Assert.Contains("inset-inline-end: 12px", foundation, StringComparison.Ordinal);
        Assert.Contains("min-width: 44px", foundation, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", foundation, StringComparison.Ordinal);
        Assert.Contains("margin-inline-start: 0 !important", foundation, StringComparison.Ordinal);
        Assert.Contains("translateX(-100%)", foundation, StringComparison.Ordinal);
        Assert.Contains("translateX(100%)", foundation, StringComparison.Ordinal);
        Assert.Contains("translateX(0)", foundation, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", foundation, StringComparison.Ordinal);
        Assert.Contains("restoreDesktopShell", js, StringComparison.Ordinal);
        Assert.Contains("clearInlineLayout", js, StringComparison.Ordinal);
        Assert.Contains("resetViewportScroll", js, StringComparison.Ordinal);
        Assert.Contains("margin-left", js, StringComparison.Ordinal);
        Assert.Contains("data-wasla-nav-owner", js, StringComparison.Ordinal);
        Assert.Contains("html[dir=\"rtl\"] body.wasla-tenant.wasla-app.wasla-tenant-shell .app-sidebar", foundation, StringComparison.Ordinal);
        Assert.Contains("right: 0 !important", foundation, StringComparison.Ordinal);
        Assert.Contains("margin-right: var(--wasla-sidebar-width) !important", foundation, StringComparison.Ordinal);
        Assert.Contains("margin-left: var(--wasla-sidebar-width) !important", foundation, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 992px)", foundation, StringComparison.Ordinal);
        Assert.Contains("direction: ltr", foundation, StringComparison.Ordinal);
        Assert.Contains("z-index: 1060 !important", foundation, StringComparison.Ordinal);
        Assert.Contains("z-index: 1050", foundation, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantShell_RtlOffCanvasPinsToInlineStartAndRestoresDesktop()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "sidebar-toggle.js");
        var liveLayout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_OrdersDisplayLayout.cshtml");

        Assert.Contains("html[dir=\"rtl\"] body.wasla-tenant.wasla-app.wasla-tenant-shell .app-sidebar", foundation, StringComparison.Ordinal);
        Assert.Contains("translateX(100%)", foundation, StringComparison.Ordinal);
        Assert.Contains("right: 0 !important", foundation, StringComparison.Ordinal);
        Assert.Contains("left: 0 !important", foundation, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 991.98px)", foundation, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 992px)", foundation, StringComparison.Ordinal);
        Assert.Contains("position: fixed", foundation, StringComparison.Ordinal);
        Assert.Contains("margin-right: var(--wasla-sidebar-collapsed-width) !important", foundation, StringComparison.Ordinal);
        Assert.Contains("margin-left: var(--wasla-sidebar-collapsed-width) !important", foundation, StringComparison.Ordinal);
        Assert.Contains("restoreDesktopShell", js, StringComparison.Ordinal);
        Assert.Contains("clearInlineLayout", js, StringComparison.Ordinal);
        Assert.Contains("resetViewportScroll", js, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-foundation.css", liveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-tenant-shell", liveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-nav-open", liveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("app-sidebar-tenant", liveLayout, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantShell_SingleLayoutAuthorityAndPointerContract()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "sidebar-toggle.js");
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");

        Assert.Contains("translateX(100%)", foundation, StringComparison.Ordinal);
        Assert.Contains("translateX(-100%)", foundation, StringComparison.Ordinal);
        Assert.Contains("transition: transform 0.2s ease, box-shadow 0.2s ease !important", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain("sidebar-expand-lg", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("layout-fixed", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("data-lte-toggle", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("adminlte.min.js", layout, StringComparison.Ordinal);
        Assert.Contains("adminlte.min.css", layout, StringComparison.Ordinal);
        var admin = Read("src", "Wasla.Web", "Areas", "Admin", "Views", "Shared", "_AdminLayout.cshtml");
        Assert.Contains("sidebar-expand-lg", admin, StringComparison.Ordinal);
        Assert.Contains("adminlte.min.js", admin, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: none !important", foundation, StringComparison.Ordinal);
        Assert.Contains("grid-area: auto !important", foundation, StringComparison.Ordinal);
        Assert.Contains("z-index: 1060 !important", foundation, StringComparison.Ordinal);
        Assert.Contains("z-index: 1050", foundation, StringComparison.Ordinal);
        Assert.Contains("pointer-events: auto !important", foundation, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none", foundation, StringComparison.Ordinal);
        Assert.Contains("wasla-shell-desktop", js, StringComparison.Ordinal);
        Assert.Contains("wasla-shell-mobile", js, StringComparison.Ordinal);
        Assert.Contains("applyShellBreakpoint", js, StringComparison.Ordinal);
        Assert.Contains("wrapper.inert = false", js, StringComparison.Ordinal);
        Assert.Contains("document.body.inert = false", js, StringComparison.Ordinal);
        Assert.DoesNotContain("wrapper.inert = true", js, StringComparison.Ordinal);
        Assert.DoesNotContain("document.body.inert = true", js, StringComparison.Ordinal);
        Assert.DoesNotContain("app-wrapper\").inert = true", js, StringComparison.Ordinal);
        Assert.DoesNotContain("new Event(\"resize\")", js, StringComparison.Ordinal);

        var revealStart = js.IndexOf("function revealSidebar()", StringComparison.Ordinal);
        var revealInert = js.IndexOf("sidebar.inert = false", revealStart, StringComparison.Ordinal);
        var revealAria = js.IndexOf("sidebar.removeAttribute(\"aria-hidden\")", revealStart, StringComparison.Ordinal);
        Assert.True(revealStart >= 0 && revealInert > revealStart && revealAria > revealInert);

        var openStart = js.IndexOf("function openNav()", StringComparison.Ordinal);
        var openReveal = js.IndexOf("revealSidebar()", openStart, StringComparison.Ordinal);
        var openChrome = js.IndexOf("setOpenChrome(true)", openStart, StringComparison.Ordinal);
        var openMain = js.IndexOf("setMainInert(true)", openStart, StringComparison.Ordinal);
        Assert.True(openStart >= 0 && openReveal > openStart && openChrome > openReveal && openMain > openChrome);

        var closeStart = js.IndexOf("function closeNav(restoreFocus)", StringComparison.Ordinal);
        var closeFocus = js.IndexOf("moveFocusToTrigger()", closeStart, StringComparison.Ordinal);
        var closeMain = js.IndexOf("setMainInert(false)", closeStart, StringComparison.Ordinal);
        var closeHide = js.IndexOf("hideSidebarAfterFocusMoved()", closeStart, StringComparison.Ordinal);
        var closeChrome = js.IndexOf("setOpenChrome(false)", closeStart, StringComparison.Ordinal);
        Assert.True(closeStart >= 0 && closeFocus > closeStart && closeMain > closeFocus && closeHide > closeMain && closeChrome > closeHide);

        Assert.Contains("id=\"tenantNavBackdrop\"", layout, StringComparison.Ordinal);
        Assert.Contains("id=\"app-sidebar-tenant\"", layout, StringComparison.Ordinal);
        Assert.True(
            layout.IndexOf("id=\"app-sidebar-tenant\"", StringComparison.Ordinal)
            < layout.IndexOf("id=\"tenant-main\"", StringComparison.Ordinal));
        Assert.DoesNotContain("inert", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantSidebar_ParentIsEmphasizedWithoutSelectedChildTreatment()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var settings = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantSettingsNav.cshtml");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Contains("printBridgeSection ? \"wasla-sidebar-nav-parent--current\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("printBridgeSection ? \"active\"", layout, StringComparison.Ordinal);
        Assert.Contains("printBridgeDevicesActive ? \"active\"", layout, StringComparison.Ordinal);
        Assert.Contains("printBridgeSetupActive ? \"active\"", layout, StringComparison.Ordinal);
        Assert.Contains("settingsActive ? \"wasla-sidebar-nav-parent--current\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsActive ? \"active\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("isUserSettings", settings, StringComparison.Ordinal);
        Assert.Contains("Active(\"/settings/users\")", layout, StringComparison.Ordinal);
        Assert.Contains("background: transparent !important", foundation, StringComparison.Ordinal);
        Assert.Contains("min-height: 2.375rem", foundation, StringComparison.Ordinal);
        Assert.Contains("border-inline-start: 1px solid var(--wasla-border)", foundation, StringComparison.Ordinal);
        Assert.Contains("wasla-sidebar-nav-parent--current", foundation, StringComparison.Ordinal);
        Assert.Contains("border-inline-start: 3px solid var(--wasla-accent)", foundation, StringComparison.Ordinal);
        Assert.Contains("--wasla-accent-soft: #FBE9DF", foundation, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", layout, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void UsersPage_KeepsSidebarLabelAndLocalizesTeamTitle()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var settings = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantSettingsNav.cshtml");
        var index = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_WaslaPageHeader.cshtml");
        var users = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Index.cshtml");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Contains("Navigation.UserSettings", layout, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(layout, "Navigation.UserSettings"));
        Assert.DoesNotContain("Navigation.UserSettings", settings, StringComparison.Ordinal);
        Assert.True(
            layout.IndexOf("Layout.Branches", StringComparison.Ordinal)
            < layout.IndexOf("Navigation.UserSettings", StringComparison.Ordinal));
        Assert.Contains("Title = L[\"TenantUsers.Title\"].Value", users, StringComparison.Ordinal);
        Assert.Contains("Description = L[\"TenantUsers.Subtitle\"].Value", users, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"wasla-page-header__title\">", index, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(index, "<h1 "));
        Assert.Contains(".wasla-col--secondary", foundation, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: break-word", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-wrap: anywhere", foundation, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1199.98px)", foundation, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 767.98px)", foundation, StringComparison.Ordinal);
        Assert.Contains(".wasla-users .wasla-page-header", foundation, StringComparison.Ordinal);
        Assert.Contains("justify-content: flex-start", foundation, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 auto", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 16rem", foundation, StringComparison.Ordinal);

        var expectedTitles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tr-TR"] = "Ekip ve Yetkiler",
            ["en-US"] = "Team & Permissions",
            ["ar-SA"] = "الفريق والصلاحيات",
            ["ru-RU"] = "Команда и права доступа"
        };
        var expectedDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tr-TR"] = "Restoran ekibinin rollerini ve erişim durumunu yönetin.",
            ["en-US"] = "Manage restaurant team roles and access status.",
            ["ar-SA"] = "أدر أدوار فريق المطعم وحالة الوصول.",
            ["ru-RU"] = "Управляйте ролями команды ресторана и статусом доступа."
        };
        var expectedNav = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tr-TR"] = "Kullanıcılar",
            ["en-US"] = "Users",
            ["ar-SA"] = "المستخدمون",
            ["ru-RU"] = "Пользователи"
        };

        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);
            Assert.Equal(expectedTitles[culture], resources["TenantUsers.Title"]);
            Assert.Equal(expectedDescriptions[culture], resources["TenantUsers.Subtitle"]);
            Assert.Equal(expectedNav[culture], resources["Navigation.UserSettings"]);
            Assert.False(string.IsNullOrWhiteSpace(resources["Layout.OpenNavigation"]));
            Assert.False(string.IsNullOrWhiteSpace(resources["Layout.CloseNavigation"]));
            Assert.False(string.IsNullOrWhiteSpace(resources["Layout.TenantNavigation"]));
        }
    }

    [Fact]
    public void ManagementScreens_ConstrainFormWidthAndKeepExistingContracts()
    {
        var createUser = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Create.cshtml");
        var connections = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PlatformConnections", "Index.cshtml");
        var createConnection = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PlatformConnections", "Create.cshtml");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "TenantUsersController.cs");

        Assert.Contains("class=\"wasla-user-create\"", createUser, StringComparison.Ordinal);
        Assert.Contains("max-width: 49rem", foundation, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.CreateTitle", createUser, StringComparison.Ordinal);
        Assert.Contains("TenantUsers.CreateSubtitle", createUser, StringComparison.Ordinal);
        Assert.Contains("asp-validation-summary=\"ModelOnly\"", createUser, StringComparison.Ordinal);
        Assert.Contains("href=\"/settings/users\"", createUser, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\"", createUser, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"FullName\"", createUser, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Email\"", createUser, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Password\"", createUser, StringComparison.Ordinal);
        Assert.Contains("[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageTenantUsers)]", controller, StringComparison.Ordinal);

        Assert.Contains("class=\"wasla-connections\"", connections, StringComparison.Ordinal);
        Assert.Contains("max-width: 64rem", foundation, StringComparison.Ordinal);
        Assert.Contains("_WaslaPageHeader", connections, StringComparison.Ordinal);
        Assert.Contains("PlatformConnections.NoteRole", connections, StringComparison.Ordinal);
        Assert.Contains("ActionUrl = \"/platform-connections/create\"", connections, StringComparison.Ordinal);
        Assert.Contains("pc-active-toggle", connections, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"pc-status-@c.Id\"", connections, StringComparison.Ordinal);
        Assert.Contains("wasla-connections__status-text", connections, StringComparison.Ordinal);
        Assert.Contains("ShowConsecutiveErrors", connections, StringComparison.Ordinal);
        Assert.Contains("ShowCircuitOpen", connections, StringComparison.Ordinal);
        Assert.Contains("id=\"platformConnectionsAntiForgery\"", connections, StringComparison.Ordinal);
        Assert.Contains("href=\"/platform-connections/@c.Id/edit\"", connections, StringComparison.Ordinal);
        Assert.Contains("PlatformConnections.NoConnectionsFound", connections, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"badge", connections, StringComparison.Ordinal);
        Assert.DoesNotContain("PlatformConnections.ErrorCount\"]</th>", connections, StringComparison.Ordinal);
        Assert.DoesNotContain("PlatformConnections.CircuitOpenUntil\"]</th>", connections, StringComparison.Ordinal);

        var toggleJs = Read("src", "Wasla.Web", "wwwroot", "js", "platform-connections", "platform-connections.js");
        Assert.Contains("toggle-active", toggleJs, StringComparison.Ordinal);
        Assert.Contains("td:nth-child(3)", toggleJs, StringComparison.Ordinal);
        Assert.Contains("wasla-connections__status-text", toggleJs, StringComparison.Ordinal);
        Assert.Contains("WaslaToast", toggleJs, StringComparison.Ordinal);

        var expectedDescriptions = new Dictionary<string, string>
        {
            ["tr-TR"] = "Bu restorana bağlı sipariş platformlarını yönetin.",
            ["en-US"] = "Manage the ordering platforms connected to this restaurant.",
            ["ar-SA"] = "أدِر منصات الطلب المتصلة بهذا المطعم.",
            ["ru-RU"] = "Управляйте платформами заказов, подключёнными к этому ресторану."
        };
        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);
            Assert.Equal(expectedDescriptions[culture], resources["PlatformConnections.NoteRole"]);
            Assert.False(string.IsNullOrWhiteSpace(resources["PlatformConnections.Active"]));
            Assert.False(string.IsNullOrWhiteSpace(resources["PlatformConnections.Inactive"]));
            Assert.False(string.IsNullOrWhiteSpace(resources["PlatformConnections.ErrorCount"]));
            Assert.False(string.IsNullOrWhiteSpace(resources["PlatformConnections.CircuitOpenUntil"]));
        }

        Assert.Contains("class=\"wasla-connection-create\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("max-width: 52rem", foundation, StringComparison.Ordinal);
        Assert.Contains("id=\"platformSelect\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("trendyol-only", createConnection, StringComparison.Ordinal);
        Assert.Contains("id=\"trendyolStoreIdHelp\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("action=\"/platform-connections/create\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("href=\"/platform-connections\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"StoreId\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"ApiKey\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"ApiSecret\"", createConnection, StringComparison.Ordinal);
        Assert.Contains("wasla-manage-form__field--wide", createConnection, StringComparison.Ordinal);

        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", foundation, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", foundation, StringComparison.Ordinal);
        Assert.Contains("margin-inline: 0", foundation, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--wasla-control-h)", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain("sidebar-toggle.js", createUser, StringComparison.Ordinal);
        Assert.DoesNotContain("sidebar-toggle.js", connections, StringComparison.Ordinal);
        Assert.DoesNotContain("sidebar-toggle.js", createConnection, StringComparison.Ordinal);
    }

    [Fact]
    public void Closeout_PreservesOrdersLiveScreenReprintAndSoundContracts()
    {
        var orders = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var live = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var liveLayout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_OrdersDisplayLayout.cshtml");
        var printJs = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-page.js");
        var audioJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-audio.js");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.DoesNotContain("css/wasla-foundation.css", liveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-tenant-shell", liveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-live-screen-card", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-orders-table", foundation, StringComparison.Ordinal);
        Assert.Contains("wasla-orders-page", orders, StringComparison.Ordinal);
        Assert.Contains("orders-page-header", orders, StringComparison.Ordinal);
        Assert.Contains("ordersLiveDisplayEnableNotificationSound", live, StringComparison.Ordinal);
        Assert.Contains("function formatMessage(template, value)", printJs, StringComparison.Ordinal);
        Assert.Contains("enableNotificationSoundFromControl", audioJs, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", foundation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HubConnection", printJs, StringComparison.Ordinal);
        Assert.False(IsDirty("docs/orders-printjob-status-signalr.md"));
        Assert.False(IsDirty("docs/temporary-receipt-test-output.md"));
    }

    [Fact]
    public void NewUsersResourceKeys_ExistInEveryCulture()
    {
        string[] keys =
        [
            "TenantUsers.SummaryRegion",
            "TenantUsers.SummaryTotal",
            "TenantUsers.SummaryActive",
            "TenantUsers.ListHeading",
            "TenantUsers.FilterPlaceholder",
            "TenantUsers.AllRoles",
            "TenantUsers.EmptyTitle",
            "TenantUsers.EmptyDescription",
            "TenantUsers.NoResultsTitle",
            "TenantUsers.NoResultsDescription",
            "TenantUsers.LastLoginNotRecorded"
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

    private static bool IsDirty(string relativePath)
    {
        var fullPath = Path.Combine(GetRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"diff --name-only -- \"{relativePath.Replace('\\', '/')}\"",
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return !string.IsNullOrWhiteSpace(output);
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
}
