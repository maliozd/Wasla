using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Models.Orders;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Tenant tables and dashboard: the shared table primitive, accessible sorting (server-side for Orders with a
/// stable order across pages, client-side for the complete Users list), and the compact dashboard.
/// </summary>
public sealed class TenantTablesAndDashboardPolishTests
{
    [Fact]
    public void SortHeader_OnTheSortedColumn_ReportsItsDirectionForAriaSort_AndTogglesIt()
    {
        var filters = new OrderFilterViewModel { SortBy = "totalAmount", SortDirection = "asc" };

        var active = OrderListUrlHelper.BuildSortHeaderLink("/orders", filters, "totalAmount", "Total");
        var other = OrderListUrlHelper.BuildSortHeaderLink("/orders", filters, "customerName", "Customer");

        Assert.True(active.IsActive);
        Assert.Equal(SortHeaderDirection.Ascending, active.Direction);
        Assert.Equal("ascending", active.AriaSort);
        Assert.Contains("sortBy=totalAmount", active.Href);
        Assert.Contains("sortDirection=desc", active.Href);

        Assert.False(other.IsActive);
        Assert.Null(other.AriaSort);
        Assert.Contains("sortDirection=asc", other.Href);
    }

    [Fact]
    public void SortHeader_DescendingColumn_ReportsDescending_AndReceivedAtStartsNewestFirst()
    {
        var filters = new OrderFilterViewModel { SortBy = "receivedAt", SortDirection = "desc" };
        var byStatus = new OrderFilterViewModel { SortBy = "status", SortDirection = "asc" };

        Assert.Equal("descending", OrderListUrlHelper.BuildSortHeaderLink("/orders", filters, "receivedAt", "Received").AriaSort);
        Assert.Contains(
            "sortDirection=desc",
            OrderListUrlHelper.BuildSortHeaderLink("/orders", byStatus, "receivedAt", "Received").Href);
    }

    [Fact]
    public void SortHeader_KeepsEveryFilterSearchAndPageSize_AndReturnsToPageOne()
    {
        var filters = new OrderFilterViewModel
        {
            Platform = FoodPlatform.GetirYemek,
            Status = OrderStatus.Preparing,
            StartDate = new DateOnly(2026, 10, 1),
            EndDate = new DateOnly(2026, 10, 3),
            Search = "  burger  ",
            Page = 4,
            PageSize = 50
        };

        var href = OrderListUrlHelper.BuildSortHeaderLink("/orders", filters, "customerName", "Customer").Href;

        Assert.StartsWith("/orders?", href);
        foreach (var part in new[]
                 {
                     "platform=GetirYemek", "status=Preparing", "startDate=2026-10-01", "endDate=2026-10-03",
                     "search=burger", "page=1", "pageSize=50", "sortBy=customerName", "sortDirection=asc"
                 })
            Assert.Contains(part, href);
    }

    [Fact]
    public async Task OrdersSortedByATiedColumn_PageWithoutDuplicatesOrGaps_NewestFirstWithinTies()
    {
        using var databases = new TenantSqlite();
        var tenantId = Guid.NewGuid();
        var start = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        var expected = new List<Guid>();
        // Same platform for every order, two pairs share a timestamp: only the tie-breakers decide the order.
        for (var i = 0; i < 9; i++)
            expected.Add(await SeedAsync(databases, tenantId, $"TY-{i}", start.AddMinutes(i / 2)));

        var reader = new OrderReadService(databases, TimeProvider.System, NullLogger<OrderReadService>.Instance);
        var seen = new List<OrderListRowSnapshot>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await reader.GetListAsync(
                tenantId, null, null, null, null, "platform", "asc", page, 4, null, CancellationToken.None);
            Assert.Equal(9, result.TotalCount);
            seen.AddRange(result.Items.Select(row => new OrderListRowSnapshot(row.Id, row.ReceivedAtUtc)));
        }

        Assert.Equal(9, seen.Count);
        Assert.Equal(expected.OrderBy(id => id), seen.Select(row => row.Id).OrderBy(id => id));
        var orderedForTies = seen
            .OrderByDescending(row => row.ReceivedAtUtc)
            .ThenBy(row => row.Id.ToString().ToUpperInvariant(), StringComparer.Ordinal)
            .Select(row => row.Id);
        Assert.Equal(orderedForTies, seen.Select(row => row.Id));

        var again = await reader.GetListAsync(
            tenantId, null, null, null, null, "platform", "asc", 2, 4, null, CancellationToken.None);
        Assert.Equal(seen.Skip(4).Take(4).Select(row => row.Id), again.Items.Select(row => row.Id));
    }

    [Fact]
    public void SortServer_StillAcceptsOnlyAllowlistedColumns()
    {
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OrdersController.cs");
        var service = Read("src", "Wasla.Infrastructure", "Services", "OrderReadService.cs");

        Assert.Contains("\"receivedAt\" or \"platform\" or \"status\" or \"customerName\" or \"totalAmount\" => s,", controller);
        Assert.Contains("_ => ascending ? q.OrderBy(o => o.ReceivedAt) : q.OrderByDescending(o => o.ReceivedAt)", service);
        Assert.Contains("q = ordered.ThenByDescending(o => o.ReceivedAt).ThenBy(o => o.Id);", service);
        Assert.DoesNotContain("EF.Property", service);
        Assert.DoesNotContain("FromSql", service);
    }

    [Fact]
    public void SortHeaderPartial_IsAnAccessibleHeaderCell_WithoutTextArrows()
    {
        var partial = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_OrderListSortHeaderLink.cshtml");

        Assert.Contains("<th scope=\"col\"", partial);
        Assert.Contains("aria-sort=\"@link.AriaSort\"", partial);
        Assert.Contains("<a class=\"wasla-table__sort", partial);
        Assert.Contains("aria-hidden=\"true\"", partial);
        Assert.DoesNotContain("↑", partial);
        Assert.DoesNotContain("↓", partial);
    }

    [Fact]
    public void OrdersTable_UsesTheSharedTable_AndKeepsItsScriptPermissionAndActionContracts()
    {
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");

        Assert.Contains("class=\"wasla-table wasla-table--stack wasla-table--stack-lg wasla-table--sortable wasla-orders-table\"", table);
        Assert.DoesNotContain("table table-sm", table);
        Assert.Contains("TenantPolicies.CanManageOrders", table);
        Assert.Contains("_OrderLifecycleActions", table);
        Assert.Contains("_OrderListPagination", table);
        Assert.Contains("_OrderStatusBadge", table);
        Assert.Contains("data-new-badge", table);
        Assert.Contains("data-order-id=\"@o.Id\"", table);
        Assert.Contains("orders-table-meta", table);
        Assert.Contains("data-order-speaker", table);
        Assert.Contains("href=\"/orders/details/@o.Id\"", table);
        Assert.Equal(5, Regex.Matches(table, "_OrderListSortHeaderLink").Count);
        // Order code and actions are deliberately not sortable.
        Assert.Contains("<th scope=\"col\">@L[\"Orders.OrderCode\"]</th>", table);
        Assert.Contains("<th scope=\"col\" class=\"wasla-table__actions\">@L[\"Orders.Actions\"]</th>", table);
        Assert.Equal(7, Regex.Matches(table, "class=\"wasla-table__cell-label\"").Count + 1);
    }

    [Fact]
    public void Dashboard_KeepsFiveFigures_AndRecentOrdersUseTheSharedTable()
    {
        var dashboard = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "Index.cshtml");

        Assert.Contains("class=\"wasla-dash-kpis mb-4\" data-tour=\"dashboard-summary\"", dashboard);
        foreach (var modifier in new[] { "orders", "revenue", "active", "cancelled", "average" })
            Assert.Contains($"(Modifier: \"{modifier}\"", dashboard);
        foreach (var key in new[]
                 {
                     "Dashboard.TodayOrders", "Dashboard.TodayRevenue", "Dashboard.ActiveOrders",
                     "Dashboard.CancelledOrders", "Dashboard.AverageOrderValue"
                 })
            Assert.Contains($"\"{key}\"", dashboard);
        Assert.Contains("class=\"wasla-table wasla-table--stack wasla-dash-table\"", dashboard);
        Assert.Contains("_OrderStatusBadge", dashboard);
        Assert.Contains("href=\"/orders/details/@o.Id\"", dashboard);
        Assert.Contains("Dashboard.NoOrdersYetToday", dashboard);
        Assert.True(
            dashboard.IndexOf("</header>", StringComparison.Ordinal) < dashboard.IndexOf("_GuidedSetupCard", StringComparison.Ordinal));
    }

    // Last login sorts on the stored UTC value (ISO 8601 in data-sort-last-login, empty when never recorded),
    // not on the culture-formatted cell text. Status and actions do not sort.
    [Fact]
    public void UsersTable_SortsNameRoleCreatedAndLastLoginWithButtons_NotStatusOrActions()
    {
        var index = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Index.cshtml");
        var details = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "Details.cshtml");
        var lastLogin = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "TenantUsers", "_LastLogin.cshtml");
        var js = Read("src", "Wasla.Web", "wwwroot", "js", "tenant-users-index.js");

        foreach (var key in new[] { "name", "role", "created", "last-login" })
            Assert.Contains($"<button type=\"button\" class=\"wasla-table__sort\" data-users-sort=\"{key}\">", index);
        Assert.Equal(4, Regex.Matches(index, "data-users-sort=").Count);
        Assert.Equal(4, Regex.Matches(index, "class=\"wasla-table__sortable").Count);
        Assert.Contains("<th scope=\"col\">@L[\"TenantUsers.Status\"]</th>", index);
        Assert.Contains("<th scope=\"col\">@L[\"TenantUsers.Actions\"]</th>", index);
        Assert.Contains("data-sort-role=\"@((int)user.Role)\"", index);
        Assert.Contains("data-sort-email=\"@user.Email\"", index);
        Assert.Contains("data-sort-last-login=\"@(user.LastLoginAt is { } lastLoginAt ? UtcTimestampPresentation.ToIso(lastLoginAt) : string.Empty)\"", index);
        Assert.Contains("lastLogin: row.getAttribute(\"data-sort-last-login\")", js);

        // List and details render the same partial: <time datetime> with the UTC value, or "Not recorded".
        Assert.Contains("<partial name=\"_LastLogin\" model=\"user.LastLoginAt\" />", index);
        Assert.Contains("<partial name=\"_LastLogin\" model=\"Model.LastLoginAt\" />", details);
        Assert.Contains("<time datetime=\"@UtcTimestampPresentation.ToIso(lastLoginAt)\">@UtcTimestampPresentation.ToDisplay(lastLoginAt)</time>", lastLogin);
        Assert.Contains("@L[\"TenantUsers.LastLoginNotRecorded\"]", lastLogin);
        foreach (var view in new[] { index, details })
        {
            Assert.DoesNotContain("LastLoginAt?.ToString", view);
            Assert.DoesNotContain("TenantUsers.LastLoginUnavailable", view);
        }
        Assert.Contains("header.setAttribute(\"aria-sort\"", js);
        Assert.Contains("header.removeAttribute(\"aria-sort\")", js);
        Assert.Contains("body.appendChild(entry.row)", js);
        Assert.DoesNotContain("innerHTML", js);
    }

    [Fact]
    public void PhoneSortBar_KeepsSortableHeadersVisible_InsteadOfClippingFocusableControls()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Contains(".wasla-table--stack.wasla-table--sortable thead th.wasla-table__sortable {", foundation);
        Assert.Contains(".wasla-users .wasla-table thead th.wasla-table__sortable {", foundation);
        Assert.Matches(@"\.wasla-users \.wasla-table thead \{\s*position: static;[^}]*clip-path: none;", foundation);
        Assert.Contains("button.wasla-table__sort {", foundation);
        Assert.True(
            foundation.LastIndexOf(".wasla-users .wasla-table thead th.wasla-table__sortable", StringComparison.Ordinal)
            > foundation.IndexOf(".wasla-users .wasla-table thead {", StringComparison.Ordinal),
            "the sort bar must come after the Users phone rules that clip the header");
    }

    [Fact]
    public void SharedTableStyles_LiveInTheFoundation_WithVisibleFocusAndLogicalAlignment()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");
        var block = foundation[foundation.IndexOf("/* Shared table additions", StringComparison.Ordinal)..foundation.IndexOf(".wasla-avatar {", StringComparison.Ordinal)];

        Assert.Contains(".wasla-table__sort:focus-visible", block);
        Assert.Contains("outline: 2px solid var(--wasla-accent);", block);
        Assert.Contains(".wasla-table--stack", block);
        Assert.Contains("@media (max-width: 767.98px)", block);
        Assert.Contains("font-variant-numeric: tabular-nums;", block);
        foreach (var physical in new[] { "left:", "right:", "text-align: left", "text-align: right", "float:" })
            Assert.DoesNotContain(physical, block);
        Assert.DoesNotContain("wasla-orders-table", foundation);
    }

    [Fact]
    public void ReadyCard_IsCompact_AndKeepsItsActions()
    {
        var setup = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-setup.css");
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_TenantSetupPanel.cshtml");

        Assert.DoesNotContain("flex-direction: column", Rule(setup, ".wasla-setup-ready {"));
        Assert.Contains("justify-content: flex-end;", Rule(setup, ".wasla-setup-ready__actions {"));
        Assert.Contains("action=\"/dashboard/complete-setup\"", panel);
        Assert.Contains("href=\"/orders/live-display\"", panel);
    }

    // Browser review: beside Platform Summary, Recent Orders hid Total/Status/Received behind a scrollbar
    // (1366px, and 1440px once orders showed full dates). Both sections now take the full row; the
    // platforms sit side by side in a responsive grid.
    [Fact]
    public void RecentOrdersAndPlatformSummary_TakeTheFullRow()
    {
        var dashboard = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "Index.cshtml");
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");

        Assert.Contains("<div class=\"col-12\" data-tour=\"dashboard-recent\">", dashboard);
        Assert.DoesNotContain("col-lg-7", dashboard);
        Assert.DoesNotContain("col-xxl-", dashboard);
        Assert.Matches(@"\.wasla-dash-platform-list \{\s*display: grid;\s*grid-template-columns: repeat\(auto-fit, minmax\(15rem, 1fr\)\);", theme);
    }

    // Browser review: older rows show the full date; it broke mid-time ("5:48" / "PM") and widened the
    // tables. Both tables share one partial that may wrap only between date and time.
    [Fact]
    public void ReceivedTimes_UseOneSharedPartial_ThatWrapsOnlyBetweenDateAndTime()
    {
        var partial = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_ReceivedAtTime.cshtml");
        var dashboard = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "Index.cshtml");
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Contains("var showDate = DateOnly.FromDateTime(Model.Local) != Model.Today;", partial);
        Assert.Equal(2, Regex.Matches(partial, "class=\"wasla-nowrap\"").Count);
        Assert.Contains("<text> </text>", partial);
        Assert.Contains("@await Html.PartialAsync(\"_ReceivedAtTime\"", dashboard);
        Assert.Contains("@await Html.PartialAsync(\"_ReceivedAtTime\"", table);
        Assert.Contains("wasla-table__datetime", dashboard);
        Assert.Contains("wasla-table__datetime", table);
        Assert.Matches(@"\.wasla-table__datetime \{\s*min-width: 5\.5rem;\s*white-space: normal;", foundation);
    }

    // Browser review: amounts and actions were start-aligned under end-aligned headers, because the base
    // ".wasla-table td" rule outranked the single-class modifiers.
    [Fact]
    public void NumbersAndActions_AreEndAligned_WithEnoughSpecificity()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Matches(@"\.wasla-table th\.wasla-table__num,\s*\.wasla-table td\.wasla-table__num \{\s*text-align: end;", foundation);
        Assert.Matches(@"\.wasla-table th\.wasla-table__actions,\s*\.wasla-table td\.wasla-table__actions \{\s*text-align: end;", foundation);
        Assert.True(
            foundation.IndexOf(".wasla-table td.wasla-table__num {", StringComparison.Ordinal)
            < foundation.IndexOf(".wasla-table--stack td.wasla-table__num,", StringComparison.Ordinal),
            "stacked layouts must still override the end alignment");
    }

    // Browser review in Arabic: a Latin customer name in an RTL cell was truncated from its start.
    [Fact]
    public void CustomerNames_IsolateTheirDirection_SoTruncationKeepsTheStartOfTheName()
    {
        var dashboard = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "Index.cshtml");
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");

        Assert.Contains("<span dir=\"auto\" title=\"@o.CustomerName\">", dashboard);
        Assert.Contains("<span dir=\"auto\" title=\"@o.CustomerName\">", table);
    }

    // Browser review at 1366px and on tablets: the Orders actions wrapped and scrolled out of view.
    [Fact]
    public void OrdersTable_KeepsActionsOnOneLine_ShortensTodaysTimes_AndUsesCardRowsOnTablets()
    {
        var table = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_OrdersTable.cshtml");
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Contains("new ReceivedAtTimeModel(o.ReceivedAtUtc, o.ReceivedAtLocal, Model.TurkeyLocalToday)", table);
        Assert.Contains("@media (min-width: 1200px) and (max-width: 1399.98px) {", theme);
        Assert.Contains("flex-wrap: nowrap;", Rule(theme, ".wasla-orders-page .wasla-orders-table-host .wasla-orders-table .wasla-orders-actions {"));
        Assert.Contains("@media (min-width: 768px) and (max-width: 1199.98px) {", foundation);
        Assert.Matches(@"\.wasla-table--stack-lg tbody tr \{\s*display: grid;\s*grid-template-columns: repeat\(3, minmax\(0, 1fr\)\);", foundation);
        Assert.Matches(@"\.wasla-table--stack-lg tbody td\.wasla-table__actions \{\s*grid-column: 1 / -1;", foundation);
    }

    // Browser review on phones: the Recent Orders (not sortable) showed an empty sort bar.
    [Fact]
    public void PlainStackedTables_KeepTheirHeaderForScreenReadersOnly()
    {
        var foundation = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-foundation.css");

        Assert.Matches(@"\.wasla-table--stack:not\(\.wasla-table--sortable\) thead \{\s*position: absolute;[^}]*clip-path: inset\(50%\);", foundation);
        Assert.Matches(@"\.wasla-table-wrap \{\s*overflow-x: auto;\s*overflow-y: hidden;", foundation);
    }

    // Browser review in Turkish and Russian: long labels or notes wrapped and moved one card's number.
    [Fact]
    public void MetricCards_KeepNumbersLevel_AndFillTabletRowsWithoutAGap()
    {
        var theme = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-theme.css");

        Assert.Matches(@"@supports \(grid-template-rows: subgrid\) \{\s*\.wasla-dash-kpi \{\s*display: grid;\s*grid-row: span 3;\s*grid-template-rows: subgrid;", theme);
        Assert.Matches(@"\.wasla-dash-kpis \{\s*grid-template-columns: repeat\(6, minmax\(0, 1fr\)\);", theme);
        Assert.Matches(@"\.wasla-dash-kpi:nth-child\(n \+ 4\) \{\s*grid-column: span 3;", theme);
        Assert.Matches(@"@media \(max-width: 575\.98px\) \{[^@]*\.wasla-dash-kpi__hint \{\s*display: none;", theme);
    }

    // Phones keep 2.75rem touch targets in the setup cards; only wider screens use the compact sizes.
    [Fact]
    public void SetupCards_CompactButtonsOnlyFromTabletWidthUp()
    {
        var setup = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-setup.css");

        Assert.Contains("min-block-size: 2.75rem;", Rule(setup, ".wasla-setup-action {"));
        Assert.Matches(@"@media \(min-width: 768px\) \{\s*\.wasla-setup-welcome__actions \.wasla-setup-action,\s*\.wasla-setup-step__action \.wasla-setup-action,\s*\.wasla-setup-ready \.wasla-setup-next \.wasla-setup-action \{\s*min-block-size: 2\.25rem;", setup);
        Assert.DoesNotMatch(@"\n\.wasla-setup-ready \.wasla-setup-action \{", setup);
    }

    private static string Rule(string css, string selectorWithBrace)
    {
        var start = css.IndexOf(selectorWithBrace, StringComparison.Ordinal);
        Assert.True(start >= 0, selectorWithBrace);
        return css[start..css.IndexOf('}', start)];
    }

    private static async Task<Guid> SeedAsync(TenantSqlite databases, Guid tenantId, string code, DateTime receivedAt)
    {
        var id = Guid.NewGuid();
        await using var db = await databases.CreateAsync(tenantId, CancellationToken.None);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = code,
            ExternalOrderCode = code,
            IdempotencyKey = $"{code}:{tenantId:N}",
            InternalStatus = OrderStatus.New,
            PlatformStatus = "Created",
            CustomerName = "Same Customer",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = receivedAt,
            ReceivedAt = receivedAt,
            RawPayloadJson = "{}",
            CreatedAt = receivedAt,
            UpdatedAt = receivedAt
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return id;
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(segments).ToArray()));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record OrderListRowSnapshot(Guid Id, DateTime ReceivedAtUtc);

    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(customerId, connection);
                using var setup = new OrdersOnlyTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new OrdersOnlyTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class OrdersOnlyTenantDbContext : TenantDbContext
    {
        public OrdersOnlyTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
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
            });
        }
    }
}
