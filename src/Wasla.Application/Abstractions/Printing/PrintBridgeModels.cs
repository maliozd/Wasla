namespace Wasla.Application.Abstractions.Printing;

public sealed record PrintBridgeAuthContext
{
    public required Guid DeviceId { get; init; }
    public required Guid TenantId { get; init; }
    public required string TenantName { get; init; }
    public required string DeviceName { get; init; }
    public Guid? InstallationId { get; init; }
    public string? MachineName { get; init; }
}

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
