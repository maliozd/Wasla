namespace Wasla.Application.Abstractions.Printing;

public sealed record PrintBridgeAuthContext(
    Guid DeviceId,
    Guid CustomerId,
    string CustomerName,
    string DeviceName,
    string? MachineName = null);

public sealed record PendingPrintJobDto(
    Guid Id,
    Guid OrderId,
    string Type,
    int CopyCount,
    string PayloadJson,
    DateTime CreatedAtUtc);

public enum PrintJobClaimResult
{
    Claimed,
    Skipped,
    NotFound
}

public sealed record PrintJobClaimResponse(PrintJobClaimResult Result, string? MessageKey);

public sealed record GeneratePrintBridgeTokenResult(
    Guid DeviceId,
    string RawToken,
    string DeviceName);
