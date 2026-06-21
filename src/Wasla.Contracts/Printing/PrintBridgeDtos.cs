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
    string CustomerName,
    string DeviceName,
    DateTime ServerTimeUtc,
    string? MachineName = null);

public sealed record ReprintPrintJobResponse(
    bool Success,
    string MessageKey,
    Guid? NewPrintJobId);

// --- Automatic setup (browser-to-application wasla-printbridge:// flow) ---

/// <summary>Desktop app exchanges the one-time setup code (single use) for its configuration.</summary>
public sealed record PrintBridgeSetupExchangeRequest(string Code);

/// <summary>
/// Configuration returned to the desktop app on a successful exchange.
/// Returned only over the authenticated exchange call; never placed in a URL or log.
/// </summary>
public sealed record PrintBridgeSetupExchangeResponse(
    Guid SessionId,
    string ServerUrl,
    string DeviceToken,
    string DeviceName,
    string CompletionCredential);

/// <summary>Desktop app reports the outcome of automatic setup, authenticated by the completion credential.</summary>
public sealed record PrintBridgeSetupCompleteRequest(
    Guid SessionId,
    string CompletionCredential,
    bool ConnectionVerified);

public sealed record PrintBridgeSetupCompleteResponse(bool Success);

