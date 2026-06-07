namespace OrderHub.Application.Abstractions.Printing;

public sealed record PrintBridgeClientInfo(
    string? BridgeName,
    string? AppVersion,
    string? PrinterName,
    string? IpAddress);

public interface IPrintBridgeAuthService
{
    Task<PrintBridgeAuthContext?> AuthenticateAsync(
        string rawToken,
        PrintBridgeClientInfo clientInfo,
        CancellationToken ct);
}
