using Wasla.Contracts.Enums;

namespace Wasla.Contracts.Orders;

public class OrderListQuery
{
    public FoodPlatformDto? Platform { get; set; }
    public OrderStatusDto? Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public record OrderListResponse(
    IReadOnlyList<OrderListItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

