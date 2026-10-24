using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Printing;

public enum ManualOrderPrintOutcome
{
    /// <summary>A first receipt job was created for the order.</summary>
    Queued = 1,

    /// <summary>A previously printed or failed receipt was sent through the existing reprint path.</summary>
    Requeued = 2,

    /// <summary>A receipt job for this order is already Pending or Printing; nothing was created.</summary>
    AlreadyQueued = 3,

    OrderNotFound = 4,
    Failed = 5
}

/// <summary>
/// Receipt print state of a single order, used to render the manual print action in its correct state.
/// Automatic and manual receipts share one <see cref="PrintJobType.Receipt" /> stream, so this state
/// reflects any receipt job for the order regardless of what created it. <see cref="Version"/> is the highest
/// <c>PrintJob.StatusVersion</c> of the order (0 without jobs): the same order-scoped value that live status
/// notifications carry, so a screen can tell whether a notification is newer than what it shows.
/// </summary>
public sealed record OrderReceiptPrintState(
    PrintJobStatus? LatestStatus,
    bool HasActiveJob,
    bool CanReprint,
    long Version = 0)
{
    public static OrderReceiptPrintState None { get; } = new(null, false, false);
}

public sealed record ManualOrderPrintResult(
    bool Success,
    ManualOrderPrintOutcome Outcome,
    string MessageKey,
    Guid? PrintJobId);

/// <summary>
/// Manual receipt printing for an order. Delegates creation to the existing receipt job and reprint
/// services so that payload building, copy-count rules, and duplicate protection are not duplicated.
/// </summary>
public interface IManualOrderPrintService
{
    Task<OrderReceiptPrintState> GetReceiptPrintStateAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct);

    /// <summary>The order's receipt print state, or null when the order does not exist in this tenant.</summary>
    Task<OrderReceiptPrintState?> FindReceiptPrintStateAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken ct);

    Task<ManualOrderPrintResult> QueueReceiptPrintAsync(
        Guid customerId,
        Guid orderId,
        string? tenantDisplayName,
        CancellationToken ct);
}

public static class ManualOrderPrintMessageKeys
{
    public const string Queued = "Orders.Print.Queued";
    public const string ReprintQueued = "Orders.Print.ReprintQueued";
    public const string AlreadyQueued = "Orders.Print.AlreadyQueued";
    public const string OrderNotFound = "Orders.Print.OrderNotFound";
    public const string Failed = "Orders.Print.Failed";
}
