using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Phase 2A: Live Screen owns dedicated operational card rendering while Orders stays table-first.
/// </summary>
public sealed class OrdersLiveScreenRenderingTests
{
    [Fact]
    public void LiveDisplay_RendersDedicatedLiveScreenPartial()
    {
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var partial = Read("Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");

        Assert.Contains("@await Html.PartialAsync(\"_LiveScreenOrders\", Model)", liveView, StringComparison.Ordinal);
        Assert.Contains("ordersLiveScreenHost", liveView, StringComparison.Ordinal);
        Assert.Contains("wasla-live-screen-card", partial, StringComparison.Ordinal);
        Assert.Contains("wasla-live-screen-card__items", partial, StringComparison.Ordinal);
        Assert.Contains("_OrderLifecycleActions", partial, StringComparison.Ordinal);
        Assert.DoesNotContain("_OrdersTable", liveView, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersIndex_StillRendersManagementTable()
    {
        var ordersView = Read("Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var table = Read("Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");

        Assert.Contains("@await Html.PartialAsync(\"_OrdersTable\", Model)", ordersView, StringComparison.Ordinal);
        Assert.Contains("ordersTableHost", ordersView, StringComparison.Ordinal);
        Assert.Contains("wasla-orders-table", table, StringComparison.Ordinal);
        Assert.DoesNotContain("_LiveScreenOrders", ordersView, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreenPartial_RespectsCanManageOrdersForActions()
    {
        var partial = Read("Areas", "Tenant", "Views", "Orders", "_LiveScreenOrders.cshtml");
        var actions = Read("Areas", "Tenant", "Views", "Orders", "_OrderLifecycleActions.cshtml");

        Assert.Contains("TenantPolicies.CanManageOrders", partial, StringComparison.Ordinal);
        Assert.Contains("CanManageOrders = canManageOrders", partial, StringComparison.Ordinal);
        Assert.Contains("Model.CanManageOrders && Model.Status", actions, StringComparison.Ordinal);
        Assert.Contains("data-order-action=\"approve\"", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("UserRole.", actions, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreenPartialEndpoint_UsesLiveScreenPolicyAndSharedQuery()
    {
        var controller = Read("Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("[HttpGet(\"live-screen\")]", controller, StringComparison.Ordinal);
        Assert.Contains("return PartialView(\"_LiveScreenOrders\", vm);", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"live-display\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = TenantPolicies.CanViewLiveScreen)]", controller, StringComparison.Ordinal);
        Assert.Equal(6, CountOccurrences(controller, "[Authorize(Policy = TenantPolicies.CanManageOrders)]"));
    }

    [Fact]
    public void OrderListQuery_GatesLineItemsToLiveScreenOnly()
    {
        var readService = ReadRepo(
            "src", "Wasla.Infrastructure", "Services", "OrderReadService.cs");
        var contract = ReadRepo(
            "src", "Wasla.Application", "Abstractions", "Orders", "IOrderReadService.cs");
        var controller = Read("Areas", "Tenant", "Controllers", "OrdersController.cs");

        Assert.Contains("bool includeLineItems = false", contract, StringComparison.Ordinal);
        Assert.Contains("bool includeLineItems = false", readService, StringComparison.Ordinal);
        Assert.Contains("if (includeLineItems)", readService, StringComparison.Ordinal);
        Assert.Contains("Array.Empty<OrderListResult.LineItem>()", readService, StringComparison.Ordinal);

        var getListStart = readService.IndexOf("public async Task<OrderListResult> GetListAsync", StringComparison.Ordinal);
        var getByIdStart = readService.IndexOf("public async Task<OrderDetailResult?> GetByIdAsync", StringComparison.Ordinal);
        Assert.True(getListStart >= 0 && getByIdStart > getListStart);
        Assert.DoesNotContain(
            "Include(o => o.Items)",
            readService.Substring(getListStart, getByIdStart - getListStart),
            StringComparison.Ordinal);

        // Live Screen + LiveDisplay request items; Orders management list/partial do not.
        Assert.Equal(2, CountOccurrences(controller, "includeLineItems: true"));
        Assert.Equal(2, CountOccurrences(controller, "includeLineItems: false"));

        var liveScreenIdx = controller.IndexOf("LiveScreenPartial", StringComparison.Ordinal);
        var liveDisplayIdx = controller.IndexOf("[HttpGet(\"live-display\")]", StringComparison.Ordinal);
        Assert.True(liveScreenIdx > 0 && liveDisplayIdx > liveScreenIdx);
        Assert.Contains("includeLineItems: true", controller.Substring(liveScreenIdx, liveDisplayIdx - liveScreenIdx), StringComparison.Ordinal);
        Assert.Contains("includeLineItems: true", controller.Substring(liveDisplayIdx, 800), StringComparison.Ordinal);
    }

    [Fact]
    public void LiveScreenPolling_UsesDedicatedPartialEndpoint()
    {
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var tableJs = ReadWwwroot("js", "orders", "orders-table.js");
        var liveJs = ReadWwwroot("js", "orders", "orders-live-display-page.js");
        var actionsJs = ReadWwwroot("js", "orders", "orders-actions.js");

        Assert.Contains("liveScreenUrl: \"/orders/live-screen\"", liveView, StringComparison.Ordinal);
        Assert.Contains("O.opts.liveScreenUrl", tableJs, StringComparison.Ordinal);
        Assert.Contains("ordersLiveScreenHost", tableJs, StringComparison.Ordinal);
        Assert.Contains("orders-live-screen-meta", tableJs, StringComparison.Ordinal);
        Assert.Contains("ordersLiveScreenHost", liveJs, StringComparison.Ordinal);
        Assert.Contains("#ordersLiveScreenHost", actionsJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase2B4_OnlyLiveScreenKeepsAViewPreference()
    {
        var ordersView = Read("Areas", "Tenant", "Views", "Orders", "Index.cshtml");
        var liveView = Read("Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var liveViewJs = ReadWwwroot("js", "orders", "orders-live-view.js");

        Assert.DoesNotContain("viewModeStorageKey", ordersView, StringComparison.Ordinal);
        Assert.DoesNotContain("Wasla.orders.viewMode", ordersView, StringComparison.Ordinal);
        Assert.Contains("viewModeStorageKey: \"Wasla.liveScreen.viewMode\"", liveView, StringComparison.Ordinal);
        Assert.Contains("O.opts.viewModeStorageKey", liveViewJs, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web" }.Concat(segments).ToArray()));

    private static string ReadRepo(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot() }.Concat(segments).ToArray()));

    private static string ReadWwwroot(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot(), "src", "Wasla.Web", "wwwroot" }.Concat(segments).ToArray()));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
