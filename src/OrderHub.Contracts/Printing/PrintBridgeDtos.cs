namespace OrderHub.Contracts.Printing;

public sealed record PendingPrintJobItemDto(
    Guid Id,
    Guid OrderId,
    string Type,
    int CopyCount,
    string PayloadJson,
    DateTime CreatedAtUtc);

public sealed record PendingPrintJobsResponse(IReadOnlyList<PendingPrintJobItemDto> Jobs);

public sealed record PrintJobActionResponse(
    bool Success,
    bool Skipped,
    string Result);

public sealed record PrintBridgeHealthResponse(
    bool Success,
    string CustomerName,
    string DeviceName,
    DateTime ServerTimeUtc);
