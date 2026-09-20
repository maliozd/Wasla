using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

public sealed class OrderLifecycleActionsModel
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; }
    public bool CanManageOrders { get; init; }
    public string ButtonSizeClass { get; init; } = "btn-sm";
}
