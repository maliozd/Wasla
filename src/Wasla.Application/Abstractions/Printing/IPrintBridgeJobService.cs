namespace Wasla.Application.Abstractions.Printing;

public interface IPrintBridgeJobService
{
    Task<IReadOnlyList<PendingPrintJobDto>> GetPendingJobsAsync(
        Guid tenantId,
        int max,
        CancellationToken ct);

    Task<PrintJobClaimResponse> TryMarkPrintingAsync(
        Guid tenantId,
        Guid jobId,
        string lockedBy,
        Guid? lockedByInstallationId,
        CancellationToken ct);

    Task<PrintJobClaimResponse> TryMarkPrintedAsync(
        Guid tenantId,
        Guid jobId,
        CancellationToken ct);

    Task<PrintJobClaimResponse> TryMarkFailedAsync(
        Guid tenantId,
        Guid jobId,
        string errorMessage,
        CancellationToken ct);
}
