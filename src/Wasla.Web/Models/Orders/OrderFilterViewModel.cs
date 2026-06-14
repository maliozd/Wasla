using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

public sealed class OrderFilterViewModel
{
    public FoodPlatform? Platform { get; set; }
    public OrderStatus? Status { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public string SortBy { get; set; } = "receivedAt";
    public string SortDirection { get; set; } = "desc";

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public string? Search { get; set; }

    public bool HasAnyFilter =>
        Platform.HasValue ||
        Status.HasValue ||
        StartDate.HasValue ||
        EndDate.HasValue ||
        !string.IsNullOrWhiteSpace(Search);
}

