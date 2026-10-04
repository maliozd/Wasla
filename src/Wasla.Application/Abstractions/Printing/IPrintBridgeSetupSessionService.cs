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
}

public sealed class PrintBridgeSetupInstallationAlreadyRegisteredException : InvalidOperationException
{
    public PrintBridgeSetupInstallationAlreadyRegisteredException()
        : base("This Print Bridge installation is already registered as an active device for the tenant.")
    {
    }
}

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
}
