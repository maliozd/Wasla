using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

public sealed class OrderLifecycleActionsModel
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; }
    /// <summary>The stored order's platform: Reject opens the reason dialog when the platform has reasons.</summary>
    public FoodPlatform Platform { get; init; }
    public bool CanManageOrders { get; init; }
    /// <summary>Order code and formatted total, shown in the confirmation dialog.</summary>
    public string OrderCode { get; init; } = string.Empty;
    public string OrderTotal { get; init; } = string.Empty;
    public string ButtonSizeClass { get; init; } = "btn-sm";
}
