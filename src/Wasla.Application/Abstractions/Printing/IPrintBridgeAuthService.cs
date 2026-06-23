namespace Wasla.Application.Abstractions.Printing;

public sealed record PrintBridgeClientInfo(
    string? BridgeName,
    string? AppVersion,
    string? PrinterName,
    string? IpAddress,
    Guid? InstallationId = null);

public interface IPrintBridgeAuthService
{
    Task<PrintBridgeAuthContext?> AuthenticateAsync(
        string rawToken,
        PrintBridgeClientInfo clientInfo,
        CancellationToken ct);
}
