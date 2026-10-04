namespace Wasla.Application.Abstractions.Printing;

public sealed record PrintBridgeClientInfo(
    string? BridgeName,
    string? AppVersion,
    string? PrinterName,
    string? IpAddress,
    Guid? InstallationId = null);

public enum PrintBridgeAuthFailureCode
{
    MissingToken,
    InvalidToken,
    DeviceRemoved,
    DeviceDisabled,
    TenantInactive,
    InstallationIdInvalid,
    InstallationIdMismatch,
    InstallationIdAlreadyBound
}

public sealed record PrintBridgeAuthResult(
    PrintBridgeAuthContext? Context,
    PrintBridgeAuthFailureCode? FailureCode)
{
    public bool Succeeded => Context is not null;

    public static PrintBridgeAuthResult Success(PrintBridgeAuthContext context) =>
        new(context, null);

    public static PrintBridgeAuthResult Failure(PrintBridgeAuthFailureCode failureCode) =>
        new(null, failureCode);
}

public interface IPrintBridgeAuthService
{
    Task<PrintBridgeAuthContext?> AuthenticateAsync(
        string rawToken,
        PrintBridgeClientInfo clientInfo,
        CancellationToken ct);

    Task<PrintBridgeAuthResult> AuthenticateDetailedAsync(
        string rawToken,
        PrintBridgeClientInfo clientInfo,
        CancellationToken ct);
}
