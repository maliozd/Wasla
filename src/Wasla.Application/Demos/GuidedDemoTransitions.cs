using Wasla.Domain.Enums;

namespace Wasla.Application.Demos;

/// <summary>
/// What a user may do to their practice order from the browser: Approve and Mark ready. As with a real
/// order, approving starts its preparation (WAS-185). Nothing else is accepted: the courier's OnTheWay and
/// Delivered belong to the platform and are simulated only by Wasla.Worker, and a practice order is not
/// rejected, so it cannot end the training.
/// </summary>
public static class GuidedDemoTransitions
{
    public const string Approve = "approve";
    public const string MarkReady = "mark-ready";

    /// <summary>Every action a browser request may apply to a practice order.</summary>
    public static IReadOnlyList<string> UserActions { get; } = [Approve, MarkReady];

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
            (OrderStatus.New, Approve) => OrderStatus.Preparing,
            // A practice order stored as Accepted before WAS-185 is in preparation too.
            (OrderStatus.Preparing or OrderStatus.Accepted, MarkReady) => OrderStatus.ReadyForPickup,
            // Mark ready is the restaurant's last action. As with a real platform order, the courier
            // reports OnTheWay and Delivered on its own (IGuidedDemoDeliverySimulator, run by Wasla.Worker).
            _ => (OrderStatus?)null
        };

        if (moved is null)
            return false;

        next = moved.Value;
        messageKey = normalized switch
        {
            Approve => "Orders.ApproveSuccess",
            MarkReady => "Orders.MarkReadySuccess",
            _ => "Orders.ActionFailed"
        };
        return true;
    }
}
