namespace OrderHub.Application.Abstractions.Printing;

public sealed record PrintBridgeDeviceSummaryDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime? LastSeenAtUtc,
    string? MachineName,
    string? PrinterName,
    string? AppVersion,
    PrintBridgeConnectionStatus ConnectionStatus)
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

public interface IPrintBridgeDeviceManagementService
{
    Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(Guid customerId, CancellationToken ct);

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

    Task<bool> UpdateDeviceNameAsync(
        Guid customerId,
        Guid deviceId,
        string deviceName,
        CancellationToken ct);
}
