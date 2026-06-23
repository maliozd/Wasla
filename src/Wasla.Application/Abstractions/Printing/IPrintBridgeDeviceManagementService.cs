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

public static class PrintBridgeDeviceNameRules
{
    public const int MaxWebDisplayNameLength = 100;
}

public interface IPrintBridgeDeviceManagementService
{
    Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(Guid customerId, CancellationToken ct);

    Task<PrintBridgeDeviceDetailsDto?> GetDeviceDetailsAsync(
        Guid customerId,
        Guid deviceId,
        CancellationToken ct);

    Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid customerId, CancellationToken ct);

    Task<GeneratePrintBridgeTokenResult> CreateDeviceAsync(
        Guid customerId,
        string deviceName,
        CancellationToken ct);

    Task<GeneratePrintBridgeTokenResult> RegenerateTokenAsync(
        Guid customerId,
        Guid deviceId,
        CancellationToken ct);

    Task<bool> SetDeviceActiveAsync(
        Guid customerId,
        Guid deviceId,
        bool isActive,
        CancellationToken ct);

    Task<RenamePrintBridgeDeviceResult> UpdateDeviceNameAsync(
        Guid customerId,
        Guid deviceId,
        string deviceName,
        CancellationToken ct);
}
