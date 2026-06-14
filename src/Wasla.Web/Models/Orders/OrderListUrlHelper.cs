namespace Wasla.Web.Models.Orders;

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

    public static SortHeaderLink BuildSortHeaderLink(
        string basePath,
        OrderFilterViewModel filters,
        string sortBy,
        string title)
    {
        var currentBy = filters.SortBy;
        var currentDir = filters.SortDirection;

        var nextDir = "asc";
        if (string.Equals(currentBy, sortBy, StringComparison.OrdinalIgnoreCase))
        {
            nextDir = string.Equals(currentDir, "asc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        }
        else
        {
            nextDir = sortBy == "receivedAt" ? "desc" : "asc";
        }

        var arrow = string.Empty;
        if (string.Equals(currentBy, sortBy, StringComparison.OrdinalIgnoreCase))
        {
            arrow = string.Equals(currentDir, "asc", StringComparison.OrdinalIgnoreCase) ? " ↑" : " ↓";
        }

        var href = Build(basePath, filters, page: 1, sortBy: sortBy, sortDirection: nextDir);
        return new SortHeaderLink(href, title, arrow);
    }
}

public sealed record SortHeaderLink(string Href, string Title, string Arrow);
