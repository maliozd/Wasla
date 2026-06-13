namespace OrderHub.Web.Models.Orders;

public static class OrderListUrlHelper
{
    public static string Build(
        string basePath,
        OrderFilterViewModel filters,
        int? page = null,
        string? sortBy = null,
        string? sortDirection = null)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        if (filters.Platform.HasValue)
            pairs.Add(new("platform", filters.Platform.Value.ToString()));
        if (filters.Status.HasValue)
            pairs.Add(new("status", filters.Status.Value.ToString()));
        if (filters.StartDate.HasValue)
            pairs.Add(new("startDate", filters.StartDate.Value.ToString("yyyy-MM-dd")));
        if (filters.EndDate.HasValue)
            pairs.Add(new("endDate", filters.EndDate.Value.ToString("yyyy-MM-dd")));
        if (!string.IsNullOrWhiteSpace(filters.Search))
            pairs.Add(new("search", filters.Search.Trim()));

        pairs.Add(new("page", (page ?? filters.Page).ToString()));
        pairs.Add(new("pageSize", filters.PageSize.ToString()));
        pairs.Add(new("sortBy", sortBy ?? filters.SortBy));
        pairs.Add(new("sortDirection", sortDirection ?? filters.SortDirection));

        var query = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")
            .ToList();

        return query.Count == 0 ? basePath : basePath + "?" + string.Join("&", query);
    }
}
