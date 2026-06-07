namespace OrderHub.Application.Abstractions.Printing;

public interface IPrintBridgeDeviceManagementService
{
    Task<GeneratePrintBridgeTokenResult> GenerateTokenAsync(
        Guid customerId,
        string deviceName,
        CancellationToken ct);
}
