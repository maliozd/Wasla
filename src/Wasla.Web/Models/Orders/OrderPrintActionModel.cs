namespace Wasla.Web.Models.Orders;

/// <summary>View model for the shared manual receipt print action rendered on order detail surfaces.</summary>
public sealed class OrderPrintActionModel
{
    public Guid OrderId { get; set; }

    /// <summary>A receipt job for this order is Pending or Printing.</summary>
    public bool InProgress { get; set; }

    /// <summary>A previous receipt job is Printed or Failed, so the action re-queues a receipt.</summary>
    public bool CanReprint { get; set; }

    public string ButtonSizeClass { get; set; } = "btn-sm";
}
