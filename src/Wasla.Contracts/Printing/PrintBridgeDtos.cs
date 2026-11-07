namespace Wasla.Contracts.Printing;

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
    string TenantName,
    string DeviceName,
    DateTime ServerTimeUtc,
    string? MachineName = null,
    Guid? InstallationId = null);

public sealed record ReprintPrintJobResponse(
    bool Success,
    string MessageKey,
    Guid? NewPrintJobId);

// --- Automatic setup (browser-to-application wasla-printbridge:// flow) ---

/// <summary>Desktop app exchanges the one-time setup code (single use) for its configuration.</summary>
public sealed record PrintBridgeSetupExchangeRequest(
    string Code,
    string? MachineName = null,
    string? PrinterName = null,
    Guid? InstallationId = null,
    string? DeviceName = null);

/// <summary>
/// Configuration returned to the desktop app on a successful exchange.
/// Returned only over the authenticated exchange call; never placed in a URL or log.
/// </summary>
public sealed record PrintBridgeSetupExchangeResponse(
    Guid SessionId,
    string ServerUrl,
    string DeviceToken,
    string DeviceName,
    Guid InstallationId,
    string CompletionCredential);

/// <summary>Desktop app reports the outcome of automatic setup, authenticated by the completion credential.</summary>
public sealed record PrintBridgeSetupCompleteRequest(
    Guid SessionId,
    string CompletionCredential,
    bool ConnectionVerified);

public sealed record PrintBridgeSetupCompleteResponse(bool Success);

/// <summary>
/// Body of the read-only move preview and of the move between tenants (WAS-91). Sent to the server Print Bridge is
/// connected to now; the current device token travels in <c>X-PrintBridge-Token</c> and the installation identity in
/// <c>X-PrintBridge-Installation-Id</c>. <see cref="ServerUrl"/> is the setup link's server. For the move, the app
/// sends the destination token and completion credential it generated; only their hashes are stored.
/// </summary>
public sealed record PrintBridgeSetupTransferRequest(
    string Code,
    string ServerUrl,
    string? DeviceToken = null,
    string? CompletionCredential = null,
    string? MachineName = null,
    string? PrinterName = null);

/// <summary>Verified names for the desktop app's confirmation. No identifier or secret.</summary>
public sealed record PrintBridgeSetupTransferPreviewResponse(
    bool SameTenant,
    string? CurrentTenantName = null,
    string? TargetTenantName = null,
    string? TargetServerUrl = null,
    DateTime? ExpiresAtUtc = null);

/// <summary>A stored move. The same answer is returned when the identical request is repeated.</summary>
public sealed record PrintBridgeSetupTransferResponse(
    Guid SessionId,
    string ServerUrl,
    string DeviceName,
    Guid InstallationId);

public sealed record UpdatePrintBridgeDeviceNameRequest(string? DeviceName);

