namespace Wasla.Application.Abstractions.Printing;

public sealed record PrintBridgeDeviceSummaryDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime? LastSeenAtUtc,
    string? MachineName,
    string? LocalAlias,
    string? PrinterName,
    string? AppVersion,
    PrintBridgeConnectionStatus ConnectionStatus)
{
    public string ConnectionStatusLabelKey =>
        PrintBridgeConnectionStatusCalculator.GetLabelKey(ConnectionStatus);

    public bool IsConnected => ConnectionStatus == PrintBridgeConnectionStatus.Connected;
}

public sealed record PrintBridgeDeviceDetailsDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastSeenAtUtc,
    string? MachineName,
    string? LocalAlias,
    string? PrinterName,
    string? AppVersion,
    PrintBridgeConnectionStatus ConnectionStatus,
    bool HasToken)
{
    public string ConnectionStatusLabelKey =>
        PrintBridgeConnectionStatusCalculator.GetLabelKey(ConnectionStatus);

    public bool IsConnected => ConnectionStatus == PrintBridgeConnectionStatus.Connected;
}

public sealed record PrintBridgeDeviceQuotaDto(
    int AllowedActiveDeviceCount,
    int ActiveDeviceCount,
    bool CanCreateActiveDevice,
    bool ActiveCountExceedsLimit);

public sealed record RenamePrintBridgeDeviceResult(
    bool Success,
    string? ErrorKey = null);

public enum RemovePrintBridgeDeviceOutcome
{
    Removed,
    AlreadyRemoved,
    NotFound,
    ActivePrintJobInProgress,
    Failed
}

public sealed record RemovePrintBridgeDeviceResult(
    RemovePrintBridgeDeviceOutcome Outcome,
    string MessageKey)
{
    public bool Success =>
        Outcome is RemovePrintBridgeDeviceOutcome.Removed
            or RemovePrintBridgeDeviceOutcome.AlreadyRemoved;
}

public static class PrintBridgeDeviceNameRules
{
    public const int MaxWebDisplayNameLength = 100;
}

public interface IPrintBridgeDeviceManagementService
{
    Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(Guid tenantId, CancellationToken ct);

    Task<PrintBridgeDeviceDetailsDto?> GetDeviceDetailsAsync(
        Guid tenantId,
        Guid deviceId,
        CancellationToken ct);

    Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid tenantId, CancellationToken ct);

    Task<GeneratePrintBridgeTokenResult> CreateDeviceAsync(
        Guid tenantId,
        string deviceName,
        CancellationToken ct);

    Task<GeneratePrintBridgeTokenResult> RegenerateTokenAsync(
        Guid tenantId,
        Guid deviceId,
        CancellationToken ct);

    Task<bool> SetDeviceActiveAsync(
        Guid tenantId,
        Guid deviceId,
        bool isActive,
        CancellationToken ct);

    Task<RemovePrintBridgeDeviceResult> RemoveDeviceAsync(
        Guid tenantId,
        Guid deviceId,
        CancellationToken ct);

    Task<RenamePrintBridgeDeviceResult> UpdateDeviceNameAsync(
        Guid tenantId,
        Guid deviceId,
        string deviceName,
        CancellationToken ct);
}

public interface IPrintBridgeActivePrintJobChecker
{
    Task<bool> HasActivePrintingJobAsync(
        Guid tenantId,
        Guid? installationId,
        string? legacyLockedBy,
        CancellationToken ct);
}
