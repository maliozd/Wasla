namespace Wasla.Application.Abstractions.Printing;

public interface IPrintBridgeJobService
{
    Task<IReadOnlyList<PendingPrintJobDto>> GetPendingJobsAsync(
        Guid customerId,
        int max,
        CancellationToken ct);

    Task<PrintJobClaimResponse> TryMarkPrintingAsync(
        Guid customerId,
        Guid jobId,
        string lockedBy,
        CancellationToken ct);

    Task<PrintJobClaimResponse> TryMarkPrintedAsync(
        Guid customerId,
        Guid jobId,
        CancellationToken ct);

    Task<PrintJobClaimResponse> TryMarkFailedAsync(
        Guid customerId,
        Guid jobId,
        string errorMessage,
        CancellationToken ct);
}
