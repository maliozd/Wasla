using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Customer;

/// <summary>
/// Pending or completed print job for a tenant order. Physical printing is handled by a future PrintAgent.
/// </summary>
public sealed class PrintJob : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public PrintJobType Type { get; set; }
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;

    public int CopyCount { get; set; } = 1;
    public string? PrinterName { get; set; }

    /// <summary>Receipt snapshot JSON captured when the job was created.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime? PrintedAt { get; set; }
}
