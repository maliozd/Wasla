using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

/// <summary>View model for the shared manual receipt print action rendered on order detail surfaces.</summary>
public sealed class OrderPrintActionModel
{
    public Guid OrderId { get; set; }

    /// <summary>A receipt job for this order is Pending or Printing.</summary>
    public bool InProgress { get; set; }

    /// <summary>A previous receipt job is Printed or Failed, so the action re-queues a receipt.</summary>
    public bool CanReprint { get; set; }

    /// <summary>Status of the order's newest receipt job, or null without one.</summary>
    public PrintJobStatus? LatestStatus { get; set; }

    /// <summary>The order-scoped receipt version this state was read at (see <see cref="OrderReceiptPrintState"/>).</summary>
    public long Version { get; set; }

    public string ButtonSizeClass { get; set; } = "btn-sm";

    /// <summary>The action's state: <c>print</c>, <c>reprint</c>, <c>queued</c> (Pending) or <c>printing</c>.</summary>
    public string Mode => InProgress
        ? (LatestStatus == PrintJobStatus.Printing ? "printing" : "queued")
        : (CanReprint ? "reprint" : "print");

    /// <summary>
    /// What became of the newest finished receipt: <c>printed</c> (Print Bridge reported it sent to the printer),
    /// <c>failed</c>, or <c>none</c> while a job is active or when there is none.
    /// </summary>
    public string Outcome => InProgress
        ? "none"
        : LatestStatus switch
        {
            PrintJobStatus.Printed => "printed",
            PrintJobStatus.Failed => "failed",
            _ => "none"
        };

    public static OrderPrintActionModel From(Guid orderId, OrderReceiptPrintState state) => new()
    {
        OrderId = orderId,
        InProgress = state.HasActiveJob,
        CanReprint = state.CanReprint,
        LatestStatus = state.LatestStatus,
        Version = state.Version
    };
}
