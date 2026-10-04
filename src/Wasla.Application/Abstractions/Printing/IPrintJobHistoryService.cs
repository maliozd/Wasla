using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Printing;

public sealed record PrintJobHistoryItemDto(
    Guid Id,
    Guid OrderId,
    string? ExternalOrderId,
    string? ExternalOrderCode,
    FoodPlatform Platform,
    PrintJobStatus Status,
    DateTime CreatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? PrintedAtUtc,
    int AttemptCount,
    string? ErrorMessage,
    string? LockedBy,
    string? OrderCustomerName,
    decimal? TotalAmount)
{
    public string StatusLabelKey => PrintJobStatusLabelKeys.Get(Status);

    public bool CanReprint =>
        Status is PrintJobStatus.Printed or PrintJobStatus.Failed;
}

public sealed record ReprintReceiptResult(
    bool Success,
    string MessageKey,
    Guid? NewPrintJobId);

public static class PrintJobHistoryLimits
{
    public const int Default = 50;
    public const int Max = 50;
}

public interface IPrintJobHistoryService
{
    Task<IReadOnlyList<PrintJobHistoryItemDto>> GetRecentReceiptJobsAsync(
        Guid customerId,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Queues a receipt using current order data and tenant template settings.
    /// Preserves the source job's copy count, clamped to the supported range of 1–3.
    /// </summary>
    Task<ReprintReceiptResult> CreateReprintAsync(
        Guid customerId,
        Guid printJobId,
        string? tenantDisplayName,
        CancellationToken ct);
}

public static class PrintJobStatusLabelKeys
{
    public static string Get(PrintJobStatus status) =>
        status switch
        {
            PrintJobStatus.Pending => "PrintBridge.PrintJobStatusPending",
            PrintJobStatus.Printing => "PrintBridge.PrintJobStatusPrinting",
            PrintJobStatus.Printed => "PrintBridge.PrintJobStatusPrinted",
            PrintJobStatus.Failed => "PrintBridge.PrintJobStatusFailed",
            PrintJobStatus.Cancelled => "PrintBridge.PrintJobStatusCancelled",
            _ => "PrintBridge.PrintJobStatusPending"
        };
}
