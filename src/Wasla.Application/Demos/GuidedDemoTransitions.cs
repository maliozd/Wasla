using Wasla.Domain.Enums;

namespace Wasla.Application.Demos;

public static class GuidedDemoTransitions
{
    public static bool TryMove(OrderStatus current, string? action, out OrderStatus next, out string messageKey)
    {
        next = current;
        messageKey = "Orders.InvalidStatusForAction";
        if (string.IsNullOrWhiteSpace(action))
        {
            messageKey = "Orders.ActionFailed";
            return false;
        }

        var normalized = action.Trim().ToLowerInvariant();
        var moved = (current, normalized) switch
        {
            (OrderStatus.New, "approve") => OrderStatus.Accepted,
            (OrderStatus.New, "reject") => OrderStatus.Cancelled,
            (OrderStatus.Accepted, "start-preparing") => OrderStatus.Preparing,
            (OrderStatus.Preparing, "mark-ready") => OrderStatus.ReadyForPickup,
            // Mark ready is the restaurant's last action. As with a real platform order, the courier
            // reports OnTheWay and Delivered on its own (IGuidedDemoDeliverySimulator, run by Wasla.Worker).
            _ => (OrderStatus?)null
        };

        if (moved is null)
            return false;

        next = moved.Value;
        messageKey = normalized switch
        {
            "approve" => "Orders.ApproveSuccess",
            "reject" => "Orders.RejectSuccess",
            "start-preparing" => "Orders.StartPreparingSuccess",
            "mark-ready" => "Orders.MarkReadySuccess",
            _ => "Orders.ActionFailed"
        };
        return true;
    }
}
