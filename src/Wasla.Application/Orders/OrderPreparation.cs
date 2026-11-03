using Wasla.Domain.Enums;

namespace Wasla.Application.Orders;

/// <summary>
/// Accepting an order starts its preparation (WAS-185): approval, auto-approval and a provider-side acceptance move a
/// New order straight to <see cref="OrderStatus.Preparing"/>, and there is no separate start-preparing step.
/// <see cref="OrderStatus.Accepted"/> keeps its persisted value for orders stored before that change. Wasla no longer
/// writes it, and it is handled as Preparing wherever an order is shown or advanced, so such an order is marked ready
/// without an extra click.
/// </summary>
public static class OrderPreparation
{
    /// <summary>The stored statuses of an order in preparation: <see cref="OrderStatus.Preparing"/> and the legacy <see cref="OrderStatus.Accepted"/>.</summary>
    public static IReadOnlyList<OrderStatus> Statuses { get; } = [OrderStatus.Preparing, OrderStatus.Accepted];

    public static bool IsPreparing(OrderStatus status) => status is OrderStatus.Preparing or OrderStatus.Accepted;

    /// <summary>The status screens show: a legacy <see cref="OrderStatus.Accepted"/> order is shown as Preparing.</summary>
    public static OrderStatus Displayed(OrderStatus status) => status == OrderStatus.Accepted ? OrderStatus.Preparing : status;
}
