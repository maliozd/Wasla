namespace Wasla.Application.Abstractions.Printing;

/// <summary>
/// Lifecycle status of an automatic Print Bridge setup session, derived from session timestamps.
/// </summary>
public enum PrintBridgeSetupSessionStatus
{
    /// <summary>Created, not yet exchanged, not expired.</summary>
    Pending,

    /// <summary>One-time code exchanged by the desktop app; awaiting completion.</summary>
    Exchanged,

    /// <summary>Desktop app reported completion (connected or saved).</summary>
    Completed,

    /// <summary>Setup failed; a new session must be created.</summary>
    Failed,

    /// <summary>Session expired before completion.</summary>
    Expired
}

/// <summary>
/// Result of creating a setup session. The raw code is returned once and never stored.
/// <para>
/// For <see cref="PrintBridgeSetupMode.NewDevice"/> sessions, <see cref="DeviceId"/> and
/// <see cref="DeviceName"/> are <c>null</c> because the device is not created until the
/// one-time-code exchange.  For <see cref="PrintBridgeSetupMode.ReconnectExistingDevice"/>
/// sessions both fields are populated from the selected device.
/// </para>
/// </summary>
public sealed record PrintBridgeSetupSessionCreated(
    Guid SessionId,
    string Code,
    DateTime ExpiresAtUtc,
    Guid? DeviceId,
    string? DeviceName);

/// <summary>
/// Result of exchanging a one-time setup code. Carries the configuration the desktop app needs.
/// Returned only over the authenticated exchange call, never in a URL or log.
/// </summary>
public sealed record PrintBridgeSetupExchangeResult(
    Guid SessionId,
    string ServerUrl,
    string DeviceToken,
    string DeviceName,
    Guid InstallationId,
    string CompletionCredential);

public enum PrintBridgeSetupMode
{
    NewDevice = 0,
    ReconnectExistingDevice = 1
}

public static class PrintBridgeSetupModeValues
{
    public const string NewDevice = "NewDevice";
    public const string ReconnectExisting = "ReconnectExisting";
}

public sealed record PrintBridgeSetupClientInfo(
    string? MachineName,
    string? AppVersion,
    string? PrinterName,
    Guid? InstallationId = null,
    string? DeviceName = null);

/// <summary>Polled status for the Web setup page.</summary>
public sealed record PrintBridgeSetupStatusDto(
    Guid SessionId,
    PrintBridgeSetupSessionStatus Status,
    DateTime ExpiresAtUtc,
    bool ConnectionVerified,
    string? FailureReason = null);

public static class PrintBridgeSetupFailureReasons
{
    public const string InstallationAlreadyRegistered = "installation_already_registered";

    /// <summary>
    /// The installation is registered (not removed) to a device of another tenant. Only the approved move, proven by
    /// that device's own valid token, can take it over (WAS-91).
    /// </summary>
    public const string InstallationRegisteredToAnotherTenant = "installation_registered_to_another_tenant";
}

public sealed class PrintBridgeSetupInstallationAlreadyRegisteredException : InvalidOperationException
{
    public PrintBridgeSetupInstallationAlreadyRegisteredException()
        : base("This Print Bridge installation is already registered as an active device for the tenant.")
    {
    }
}

public sealed class PrintBridgeSetupInstallationRegisteredToAnotherTenantException : InvalidOperationException
{
    public PrintBridgeSetupInstallationRegisteredToAnotherTenantException()
        : base("This Print Bridge installation is registered to another tenant's device.")
    {
    }
}

/// <summary>
/// A request to move one Print Bridge installation from the tenant of its current device to the tenant of a setup
/// session (WAS-91). It carries both proofs, the current device's own token and the destination's one-time setup code,
/// bound to the installation and to the setup link's server.
/// </summary>
/// <param name="SourceDeviceToken">The token of the device the installation uses now (the source proof).</param>
/// <param name="InstallationId">The desktop installation identity the source device must be bound to.</param>
/// <param name="Code">The destination session's one-time setup code (the destination proof).</param>
/// <param name="SetupServerUrl">The server of the setup link; its origin must be the session's.</param>
/// <param name="NewDeviceToken">
/// Move only: the token the desktop app generated for the destination device. Only its hash is stored. Because the app
/// holds every secret before it asks, it can repeat the identical request after a lost response.
/// </param>
/// <param name="CompletionCredential">Move only: the app-generated credential for the existing completion call.</param>
public sealed record PrintBridgeTransferRequest(
    string SourceDeviceToken,
    Guid? InstallationId,
    string Code,
    string? SetupServerUrl,
    string? NewDeviceToken = null,
    string? CompletionCredential = null,
    string? MachineName = null,
    string? AppVersion = null,
    string? PrinterName = null);

public enum PrintBridgeTransferOutcome
{
    /// <summary>Preview: a move is possible and needs the user's approval. Move: done, or the identical move was already done.</summary>
    Ready,

    /// <summary>The setup session belongs to the device's own tenant: the ordinary exchange applies, not a move.</summary>
    SameTenant,

    /// <summary>The code is unknown, expired or used, or the setup link's server is not the session's.</summary>
    InvalidOrExpired,

    /// <summary>The current device token is missing, unknown, revoked or disabled, or bound to another installation.</summary>
    SourceRejected,

    /// <summary>The source tenant still has a receipt job in Printing for this installation (WAS-58/56).</summary>
    PrintingInProgress,

    /// <summary>The destination tenant has no room for another active device.</summary>
    DeviceLimitReached,

    /// <summary>
    /// The installation is also registered to a device the move may not retire, or the destination device is bound to
    /// another installation.
    /// </summary>
    InstallationConflict,

    /// <summary>The destination's reconnect device is turned off (passive); a move would end on a device that cannot print.</summary>
    DestinationDeviceDisabled,

    /// <summary>An unexpected failure; nothing was changed.</summary>
    Failed
}

/// <summary>Read-only preview of a move: verified names and the session's server only, never a secret or an identifier.</summary>
public sealed record PrintBridgeTransferPreview(
    PrintBridgeTransferOutcome Outcome,
    string? CurrentTenantName = null,
    string? TargetTenantName = null,
    string? TargetServerUrl = null,
    DateTime? ExpiresAtUtc = null);

public sealed record PrintBridgeTransferResult(
    PrintBridgeTransferOutcome Outcome,
    Guid? SessionId = null,
    string? ServerUrl = null,
    string? DeviceName = null,
    Guid? InstallationId = null);

public sealed class PrintBridgeSetupTokenReplacementConfirmationRequiredException : InvalidOperationException
{
    public PrintBridgeSetupTokenReplacementConfirmationRequiredException()
        : base("Replacing an existing active Print Bridge device token requires explicit confirmation.")
    {
    }
}

public sealed class PrintBridgeSetupDeviceSelectionRequiredException : InvalidOperationException
{
    public PrintBridgeSetupDeviceSelectionRequiredException()
        : base("Reconnect setup requires an explicit Print Bridge device selection.")
    {
    }
}

public sealed class PrintBridgeSetupDeviceNotFoundException : InvalidOperationException
{
    public PrintBridgeSetupDeviceNotFoundException()
        : base("Selected Print Bridge device was not found for the tenant.")
    {
    }
}

/// <summary>
/// Coordinates the short-lived, single-use automatic setup session used by the
/// browser-to-application (<c>wasla-printbridge://</c>) Print Bridge connect flow.
/// </summary>
public interface IPrintBridgeSetupSessionService
{
    /// <summary>
    /// Create a short-lived setup session. New-device sessions are not bound to a
    /// Print Bridge device until the desktop app successfully exchanges the one-time code.
    /// Reconnect sessions require an explicit existing <paramref name="deviceId"/>.
    /// </summary>
    Task<PrintBridgeSetupSessionCreated> CreateSessionAsync(
        Guid tenantId,
        PrintBridgeSetupMode setupMode,
        Guid? deviceId,
        string serverUrl,
        string? defaultDeviceName,
        bool confirmReplaceActiveToken,
        CancellationToken ct);

    /// <summary>
    /// Atomically exchange a one-time code (single-use). Regenerates the bound device's token and
    /// returns the configuration for the app. Returns null for invalid/expired/used codes.
    /// </summary>
    Task<PrintBridgeSetupExchangeResult?> ExchangeAsync(
        string rawCode,
        PrintBridgeSetupClientInfo clientInfo,
        CancellationToken ct);

    /// <summary>
    /// Record completion of a session, authenticated by the completion credential issued at exchange.
    /// </summary>
    Task<bool> CompleteAsync(
        Guid sessionId,
        string completionCredential,
        bool connectionVerified,
        CancellationToken ct);

    /// <summary>Get the current status of a session owned by the tenant (for Web polling).</summary>
    Task<PrintBridgeSetupStatusDto?> GetStatusAsync(Guid tenantId, Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Read-only check of a move between tenants (WAS-91) for the desktop app's confirmation: verifies both proofs and
    /// returns the verified tenant names. Does not use the code, writes nothing, and creates or retires no device.
    /// </summary>
    Task<PrintBridgeTransferPreview> PreviewTransferAsync(PrintBridgeTransferRequest request, CancellationToken ct);

    /// <summary>
    /// Moves the installation in one transaction: uses the code, creates or binds the destination device with the
    /// app-generated token, and retires the source device, whose token stops working at once while its record stays.
    /// The identical request repeated before completion returns the same result and changes nothing.
    /// </summary>
    Task<PrintBridgeTransferResult> TransferAsync(PrintBridgeTransferRequest request, CancellationToken ct);
}
