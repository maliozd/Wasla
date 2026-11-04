using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

public sealed class OrderLifecycleActionsModel
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; }
    /// <summary>The stored order's platform: Reject opens the reason dialog when the platform has reasons.</summary>
    public FoodPlatform Platform { get; init; }
    public bool CanManageOrders { get; init; }
    public string ButtonSizeClass { get; init; } = "btn-sm";
}
