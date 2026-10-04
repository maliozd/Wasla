using Microsoft.AspNetCore.Routing;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Admin;

namespace Wasla.Web.Areas.Admin.Models.TenantOperations;

public sealed class AdminOverviewViewModel
{
    /// <summary>Null when CentralDb could not be read; the page then shows the load-error panel.</summary>
    public TenantOperationsOverview? Overview { get; init; }

    public IReadOnlyList<AdminPendingRegistrationListItemDto> AttentionRegistrations { get; init; } =
        Array.Empty<AdminPendingRegistrationListItemDto>();
}

public sealed class AdminTenantListViewModel
{
    /// <summary>Null when CentralDb could not be read; the page then shows the load-error panel.</summary>
    public TenantListPage? Page { get; init; }

    public TenantListQuery Query { get; init; } = TenantListQuery.Default;

    public IReadOnlyList<string> PlanCodes { get; init; } = Array.Empty<string>();

    public DateTime NowUtc { get; init; }

    /// <summary>
    /// Route values for a list link. Search, filters and page size are always kept; sorting or filtering again
    /// returns to the first page.
    /// </summary>
    public RouteValueDictionary RouteValues(
        TenantListSortField? sort = null,
        TenantListSortDirection? direction = null,
        int? page = null)
    {
        var values = new RouteValueDictionary();
        if (Query.Search is { } search)
            values["q"] = search;
        if (TenantListQuery.StatusToken(Query.Status) is { } status)
            values["status"] = status;
        if (TenantListQuery.MigrationToken(Query.Migration) is { } migration)
            values["migration"] = migration;
        if (Query.Plan is { } plan)
            values["plan"] = plan;

        values["sort"] = TenantListQuery.SortToken(sort ?? Query.Sort);
        values["dir"] = TenantListQuery.DirectionToken(direction ?? Query.Direction);
        if (Query.PageSize != TenantListQuery.DefaultPageSize)
            values["size"] = Query.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var targetPage = page ?? 1;
        if (targetPage > 1)
            values["page"] = targetPage.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return values;
    }

    /// <summary>Link for a column header: the current column toggles, another column starts at its natural order.</summary>
    public RouteValueDictionary SortRouteValues(TenantListSortField field)
    {
        var direction = field == Query.Sort
            ? (Query.Direction == TenantListSortDirection.Ascending
                ? TenantListSortDirection.Descending
                : TenantListSortDirection.Ascending)
            : TenantListQuery.DefaultDirection(field);
        return RouteValues(field, direction);
    }

    /// <summary>The <c>aria-sort</c> value for a column header, or null for an unsorted column.</summary>
    public string? AriaSort(TenantListSortField field) =>
        field != Query.Sort
            ? null
            : Query.Direction == TenantListSortDirection.Ascending ? "ascending" : "descending";
}

public sealed class AdminTenantDetailViewModel
{
    public TenantOperationsDetail? Detail { get; init; }

    public TenantOperationalHealth? Health { get; init; }

    public IReadOnlyList<TenantOperationsGuidance> Guidance { get; init; } = Array.Empty<TenantOperationsGuidance>();

    /// <summary>The tenant's own address, built with the shared tenant URL builder.</summary>
    public string? TenantAddressUrl { get; init; }

    public DateTime NowUtc { get; init; }
}
