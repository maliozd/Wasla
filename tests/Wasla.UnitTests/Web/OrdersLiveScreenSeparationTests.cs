using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 2B4: Orders is the management search/history page and Live Screen is the operational display.
/// </summary>
public sealed class OrdersLiveScreenSeparationTests
{
    private static readonly string[] LocalizedCultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public void Sidebar_OpensLiveScreenDirectlyInANewTab()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");

        var liveIndex = layout.IndexOf("asp-action=\"LiveDisplay\"", StringComparison.Ordinal);
        Assert.True(liveIndex > 0, "Live Screen sidebar link was not found.");

        var linkBlock = layout.Substring(liveIndex, Math.Min(240, layout.Length - liveIndex));
        Assert.Contains("target=\"_blank\"", linkBlock, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", linkBlock, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanViewLiveScreen", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Sidebar_NoLongerExposesASeparateOrderHistoryEntry()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");

        Assert.DoesNotContain("asp-action=\"History\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("@L[\"Layout.OrderHistory\"]", layout, StringComparison.Ordinal);
        Assert.Contains("asp-action=\"Index\"", layout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task LiveScreenSidebarVisibility_StillFollowsCanViewLiveScreenPolicy(UserRole role)
    {
        var authorization = BuildAuthorizationService(_tenantId);
        var navigation = new TenantNavigationAuthorizationService(authorization);

        var permissions = await navigation.GetPermissionsAsync(Principal(_tenantId, role));
        var policy = await authorization.AuthorizeAsync(Principal(_tenantId, role), resource: null, TenantPolicies.CanViewLiveScreen);

        Assert.Equal(policy.Succeeded, permissions.CanViewLiveScreen);
    }

    [Fact]
    public void LegacyOrderHistoryRoute_RedirectsToOrdersAndPreservesFilters()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("[HttpGet(\"history\")]", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("View(\"History\"", controller, StringComparison.Ordinal);

        var historyIndex = controller.IndexOf("public IActionResult History(", StringComparison.Ordinal);
        Assert.True(historyIndex > 0, "History redirect action was not found.");

        var block = controller.Substring(historyIndex, controller.IndexOf("[HttpGet(\"sync-settings\")]", StringComparison.Ordinal) - historyIndex);
        foreach (var key in new[] { "platform", "status", "startDate", "endDate", "search", "sortBy", "sortDirection", "page", "pageSize" })
        {
            Assert.Contains($"new(\"{key}\"", block, StringComparison.Ordinal);
        }

        Assert.Contains("return Redirect(query.Count == 0 ? \"/orders\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFullscreenRedirect_AndLiveScreenPolicyRemainIntact()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("if (IsLegacyFullscreenRequest())", controller, StringComparison.Ordinal);
        Assert.Contains("return RedirectToAction(nameof(LiveDisplay));", controller, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = TenantPolicies.CanViewLiveScreen)]", controller, StringComparison.Ordinal);
        Assert.Contains("[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]", controller, StringComparison.Ordinal);
        Assert.Equal(6, CountOccurrences(controller, "[Authorize(Policy = TenantPolicies.CanManageOrders)]"));
    }

    [Fact]
    public void Orders_SupportsServerSideSearchAndPagingWithoutLiveBehaviour()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var ordersView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var pageJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-page.js");

        var indexIndex = controller.IndexOf("public async Task<IActionResult> Index(", StringComparison.Ordinal);
        Assert.True(indexIndex > 0);
        var indexBlock = controller.Substring(indexIndex, controller.IndexOf("[HttpGet(\"history\")]", StringComparison.Ordinal) - indexIndex);
        Assert.Contains("[FromQuery] string? search", indexBlock, StringComparison.Ordinal);
        Assert.Contains("endDate, search,", indexBlock, StringComparison.Ordinal);

        Assert.Contains("name=\"search\"", ordersView, StringComparison.Ordinal);
        Assert.Contains("name=\"pageSize\"", ordersView, StringComparison.Ordinal);

        // Live operational behaviour is gone from the management page.
        Assert.DoesNotContain("pollingIntervalMs", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("orders-automation-status.js", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("automationStatusSync", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("Orders.ManageSettings", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("initPolling", pageJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Orders_KeepsCsrfProtectedLifecycleActionsAndTenantScopedQuery()
    {
        var ordersView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("@Html.AntiForgeryToken()", ordersView, StringComparison.Ordinal);
        Assert.Contains("orders-actions.js", ordersView, StringComparison.Ordinal);
        Assert.Contains("TenantPolicies.CanManageOrders", table, StringComparison.Ordinal);
        Assert.Contains("_OrderLifecycleActions", table, StringComparison.Ordinal);
        Assert.Contains("tenant.Id, platform, status, startDate, endDate, search,", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersTable_SortLinksPreserveSearchAndPageSizeThroughSharedUrlBuilder()
    {
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");
        var urlHelper = Read("src", "Wasla.Web", "Models", "Orders", "OrderListUrlHelper.cs");

        Assert.Contains("_OrderListSortHeaderLink", table, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildOrdersUrl", table, StringComparison.Ordinal);
        Assert.Contains("new(\"search\", filters.Search.Trim())", urlHelper, StringComparison.Ordinal);
        Assert.Contains("new(\"pageSize\", filters.PageSize.ToString())", urlHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersTableFragment_AcceptsTheSameFilterSetAsIndex()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");

        var tableIndex = controller.IndexOf("public async Task<IActionResult> Table(", StringComparison.Ordinal);
        Assert.True(tableIndex > 0);
        var block = controller.Substring(tableIndex, controller.IndexOf("[HttpGet(\"live-screen\")]", StringComparison.Ordinal) - tableIndex);

        // The refresh fragment must not silently drop a filter the page is using.
        foreach (var parameter in new[]
        {
            "[FromQuery] FoodPlatform? platform",
            "[FromQuery] OrderStatus? status",
            "[FromQuery] string? startDate",
            "[FromQuery] string? endDate",
            "[FromQuery] string? search",
            "[FromQuery] string? sortBy",
            "[FromQuery] string? sortDirection",
            "[FromQuery] int page",
            "[FromQuery] int pageSize"
        })
        {
            Assert.Contains(parameter, block, StringComparison.Ordinal);
        }

        Assert.Contains("endDate, search,", block, StringComparison.Ordinal);
        Assert.DoesNotContain("search: null", block, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersPagination_LivesInsideTheRefreshedFragment()
    {
        var ordersView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");
        var tableJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-table.js");

        // A lifecycle action replaces #ordersTableHost only, so pagination must be part of that fragment
        // or it would keep showing a stale page count once the row leaves the active filter.
        Assert.Contains("_OrderListPagination", table, StringComparison.Ordinal);
        Assert.DoesNotContain("_OrderListPagination", ordersView, StringComparison.Ordinal);
        Assert.Contains("container.innerHTML = html;", tableJs, StringComparison.Ordinal);
        Assert.Contains("getElementById(\"ordersTableHost\")", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedHistoryPage_LeavesNoStaleBackLinksOrViewState()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var details = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Details.cshtml");
        var detailVm = Read("src", "Wasla.Web", "Models", "Orders", "OrderDetailViewModel.cs");
        var listVm = Read("src", "Wasla.Web", "Models", "Orders", "OrderListViewModel.cs");

        Assert.DoesNotContain("BackFromHistory", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("BackFromHistory", details, StringComparison.Ordinal);
        Assert.DoesNotContain("BackFromHistory", detailVm, StringComparison.Ordinal);
        Assert.DoesNotContain("IsHistoryPage", listVm, StringComparison.Ordinal);
        Assert.Contains("BackUrl = \"/orders\",", controller, StringComparison.Ordinal);

        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);
            Assert.False(resources.ContainsKey("Layout.OrderHistory"), $"Layout.OrderHistory still present for {culture}.");
            Assert.False(resources.ContainsKey("Orders.Details.BackToOrderHistory"), $"Orders.Details.BackToOrderHistory still present for {culture}.");
        }
    }

    [Fact]
    public void OrphanedSharedViewModeModule_IsGone()
    {
        var root = GetRepositoryRoot();
        var tableJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-table.js");

        Assert.False(
            File.Exists(Path.Combine(root, "src", "Wasla.Web", "wwwroot", "js", "orders", "orders-view-mode.js")),
            "orders-view-mode.js is no longer loaded by any view and should be removed.");
        Assert.DoesNotContain("O.viewMode", tableJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_OwnsMovedOperationalControlsBehindExistingPermissions()
    {
        var liveView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        Assert.Contains("automationStatusSync", liveView, StringComparison.Ordinal);
        Assert.Contains("automationStatusAutoApprove", liveView, StringComparison.Ordinal);
        Assert.Contains("automationStatusReceipt", liveView, StringComparison.Ordinal);
        Assert.Contains("orders-automation-status.js", liveView, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManageOrderSettings", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.ManageSettings\"]", liveView, StringComparison.Ordinal);

        // Read-only status summary reuses the existing endpoints; no alternate backend.
        Assert.Contains("orderSyncSettingsUrl: \"/orders/sync-settings\"", liveView, StringComparison.Ordinal);
        Assert.Contains("orderSettingsUrl: \"/orders/order-settings\"", liveView, StringComparison.Ordinal);

        // Historical filters stay on Orders.
        Assert.DoesNotContain("name=\"search\"", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"startDate\"", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_ViewSwitchRendersOneCardTreeAndPersistsChoice()
    {
        var liveView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var liveViewJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-view.js");
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");

        Assert.DoesNotContain("data-live-screen-view=\"cards\"", liveView, StringComparison.Ordinal);
        Assert.Contains("data-live-screen-view=\"list\"", liveView, StringComparison.Ordinal);
        Assert.Contains("data-live-screen-view=\"board\"", liveView, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(liveView, "Html.PartialAsync(\"_LiveScreenOrders\", Model)"));

        Assert.Contains("localStorage.setItem(storageKey(), view)", liveViewJs, StringComparison.Ordinal);
        Assert.Contains("wasla-live-screen-host--list", liveViewJs, StringComparison.Ordinal);
        Assert.Contains(".wasla-live-screen-host--list .wasla-live-screen-card", css, StringComparison.Ordinal);
        Assert.Contains(".wasla-live-screen-host.wasla-live-screen-host--board", css, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto;", css, StringComparison.Ordinal);

        // A view switch must not re-fetch orders or reset live state.
        Assert.DoesNotContain("fetch(", liveViewJs, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshOrdersTable", liveViewJs, StringComparison.Ordinal);
        Assert.DoesNotContain("recentlyNewOrderIds", liveViewJs, StringComparison.Ordinal);
        Assert.DoesNotContain("knownOrderIds", liveViewJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_DetailModalIsLazyReusableAndServerAuthorized()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var liveView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var card = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderDetailPanel.cshtml");
        var modalJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-detail-modal.js");

        Assert.Contains("[HttpGet(\"{id:guid}/detail-panel\")]", controller, StringComparison.Ordinal);
        Assert.Contains("return PartialView(\"_OrderDetailPanel\", vm);", controller, StringComparison.Ordinal);
        Assert.Contains("_orders.GetByIdAsync(tenant.Id, id, ct)", controller, StringComparison.Ordinal);

        // The card carries a trigger only; detail markup is not rendered per order.
        Assert.Contains("data-order-detail=\"@o.Id\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("_OrderDetailPanel", card, StringComparison.Ordinal);

        Assert.Equal(1, CountOccurrences(liveView, "id=\"ordersLiveDetailModal\""));
        Assert.Contains("getOrCreateInstance", modalJs, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener(\"click\"", modalJs, StringComparison.Ordinal);
        Assert.Contains("openDetail(orderId, trigger)", modalJs, StringComparison.Ordinal);
        Assert.Contains("restoreDetailFocus()", modalJs, StringComparison.Ordinal);
        Assert.Contains("anotherModalIsOpen()", modalJs, StringComparison.Ordinal);

        // Lifecycle rules stay server-side: the panel renders the shared partial under CanManageOrders.
        Assert.Contains("TenantPolicies.CanManageOrders", panel, StringComparison.Ordinal);
        Assert.Contains("_OrderLifecycleActions", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderStatus.Accepted", modalJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreen_DetailModalDoesNotAccumulateHandlersPerOpen()
    {
        var modalJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-detail-modal.js");

        // Listeners are registered once (init + bootstrap), never inside open/load.
        var initIndex = modalJs.IndexOf("function init()", StringComparison.Ordinal);
        var openIndex = modalJs.IndexOf("function openDetail(", StringComparison.Ordinal);
        Assert.True(initIndex > 0 && openIndex > 0);

        var beforeInit = modalJs.Substring(0, initIndex);
        Assert.Equal(0, CountOccurrences(beforeInit, "addEventListener("));
        Assert.Equal(4, CountOccurrences(modalJs, "addEventListener("));
        Assert.DoesNotContain("body.addEventListener", modalJs, StringComparison.Ordinal);
        Assert.Contains("client().cancel(\"modal\")", modalJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(modalJs, "async function loadDetail("));
    }

    [Fact]
    public void BrowserNotificationOwnership_RemainsLiveScreenOnly()
    {
        var tableJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-table.js");
        var ordersView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var liveView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        var storeJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-store.js");
        Assert.Contains("showBrowserNotificationIfAllowed()", storeJs, StringComparison.Ordinal);
        Assert.DoesNotContain("showBrowserNotificationIfAllowed()", tableJs, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(storeJs, "showBrowserNotificationIfAllowed()"));
        Assert.DoesNotContain("newOrderArrived", ordersView, StringComparison.Ordinal);
        Assert.Contains("newOrderArrived", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void RelocatedAndNewLiveScreenText_IsLocalizedInEveryCulture()
    {
        foreach (var culture in LocalizedCultures)
        {
            var resources = ReadResourceValues(culture);

            foreach (var key in new[]
            {
                "Orders.LiveScreen.ViewCards",
                "Orders.LiveScreen.ViewList",
                "Orders.LiveScreen.ViewFocus",
                "Orders.LiveScreen.ColumnOrder",
                "Orders.LiveScreen.ColumnElapsed",
                "Orders.LiveScreen.ListTotal",
                "Orders.LiveScreen.ItemNote",
                "Orders.LiveScreen.SelectOrder",
                "Orders.LiveScreen.SelectOrderDescription",
                "Orders.LiveScreen.NoDisplayableOrders",
                "Orders.LiveScreen.NoDisplayableOrdersDescription",
                "Orders.LiveScreen.BackToQueue",
                "Orders.LiveScreen.DetailRetry",
                "Orders.LiveScreen.Queue",
                "Orders.LiveScreen.DetailLoadFailed",
                "Orders.LiveScreen.ConnectionStale",
                "Orders.LiveScreen.SessionExpired",
                "Orders.LiveScreen.CountUnavailable",
                "Orders.ManageSettings",
                "Orders.History.Search",
                "Orders.History.SearchPlaceholder",
                "Orders.History.PageSize",
                "Orders.History.TotalResults",
                "Orders.History.ViewDetails"
            })
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{key} missing for {culture}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} empty for {culture}.");
            }
        }
    }

    [Fact]
    public void LiveScreenAndOrdersViews_DoNotHardCodeUserFacingText()
    {
        var liveView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var ordersView = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderDetailPanel.cshtml");

        Assert.Contains("@L[\"Orders.LiveScreen.ViewBoard\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.LiveScreen.ViewList\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.LiveScreen.ViewFocus\"]", liveView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.History.Search\"]", ordersView, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.Details.OpenFullOrder\"]", panel, StringComparison.Ordinal);

        Assert.DoesNotContain(">Cards<", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain(">List<", liveView, StringComparison.Ordinal);
        Assert.DoesNotContain(">Search<", ordersView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveDetailModal_ShowsOrderNoteAndLeavesCurrencyToTheSharedFormatter()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrderDetailPanel.cshtml");
        var helper = Read("src", "Wasla.Web", "Ui", "OrderUiHelper.cs");
        var details = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "Details.cshtml");
        var viewModel = Read("src", "Wasla.Web", "Models", "Orders", "OrderDetailViewModel.cs");
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var detail = Read("src", "Wasla.Application", "Abstractions", "Orders", "OrderDetailResult.cs");
        var readService = Read("src", "Wasla.Infrastructure", "Services", "OrderReadService.cs");
        var modalJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-detail-modal.js");
        var storeJs = Read("src", "Wasla.Web", "wwwroot", "js", "orders", "orders-live-store.js");
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");

        var noteAt = panel.IndexOf("wasla-live-detail-order-note", StringComparison.Ordinal);
        var bodyAt = panel.IndexOf("wasla-live-detail__body", StringComparison.Ordinal);
        var itemNoteAt = panel.IndexOf("wasla-live-detail__note", StringComparison.Ordinal);
        Assert.True(noteAt > 0 && bodyAt > noteAt && itemNoteAt > bodyAt);

        Assert.Contains("!string.IsNullOrWhiteSpace(Model.CustomerNote)", panel, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.LiveScreen.OrderNote\"]", panel, StringComparison.Ordinal);
        Assert.Contains("@Model.CustomerNote", panel, StringComparison.Ordinal);
        Assert.Contains("@i.Notes", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("data-order-note", panel, StringComparison.Ordinal);
        Assert.Contains("public string? CustomerNote { get; set; }", viewModel, StringComparison.Ordinal);
        Assert.Contains("public string? CustomerNote { get; init; }", detail, StringComparison.Ordinal);
        Assert.Contains("CustomerNote = order.CustomerNote", controller, StringComparison.Ordinal);
        Assert.Contains(
            "CustomerNote = string.IsNullOrWhiteSpace(order.CustomerNote) ? null : order.CustomerNote.Trim()",
            readService,
            StringComparison.Ordinal);

        Assert.Contains("data-money=\"@InvariantAmount(i.TotalPrice)\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-money=\"@InvariantAmount(o.Price)\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-money-wrap=\"surcharge\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-money=\"@InvariantAmount(Model.DeliveryFee)\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-money=\"@InvariantAmount(Model.ServiceFee)\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-money=\"@InvariantAmount(Model.TotalAmount)\"", panel, StringComparison.Ordinal);
        Assert.Contains("OrderUiHelper.FormatAmount", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("ToString(\"C\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("₺", panel, StringComparison.Ordinal);
        Assert.DoesNotContain(" TL", panel, StringComparison.Ordinal);
        Assert.Contains("amount.ToString(\"N2\", CultureInfo.CurrentCulture)", helper, StringComparison.Ordinal);
        Assert.Contains("ToString(\"C\"", details, StringComparison.Ordinal);

        Assert.Contains("consumer !== \"modal\" && consumer !== \"focus\"", modalJs, StringComparison.Ordinal);
        Assert.Contains("O.applyDetailCurrency(container, culture)", modalJs, StringComparison.Ordinal);
        Assert.Contains("function applyDetailCurrency", storeJs, StringComparison.Ordinal);
        Assert.Contains("formatAmount(raw, culture)", storeJs, StringComparison.Ordinal);
        Assert.Contains("String(raw).trim() === \"\"", storeJs, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", theme.Substring(theme.IndexOf(".wasla-live-detail-order-note__text", StringComparison.Ordinal), 280), StringComparison.Ordinal);
        Assert.Contains(".wasla-live-focus .wasla-live-detail-order-note { display: none; }", theme, StringComparison.Ordinal);
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
        var path = Path.Combine(GetRepositoryRoot(), "src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx");

        return System.Xml.Linq.XDocument.Load(path)
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
