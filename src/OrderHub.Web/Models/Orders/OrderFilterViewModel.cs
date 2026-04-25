using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.Orders;

public sealed class OrderFilterViewModel
{
    public FoodPlatform? Platform { get; set; }
    public OrderStatus? Status { get; set; }
    public DateTime? StartDateUtc { get; set; }
    public DateTime? EndDateUtc { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

