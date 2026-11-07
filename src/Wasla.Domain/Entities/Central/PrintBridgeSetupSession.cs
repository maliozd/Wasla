using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Short-lived, single-use automatic setup session for connecting a Wasla Print Bridge
/// device through the browser-to-application (<c>wasla-printbridge://</c>) flow.
/// Lives in CentralDb only.
///
/// The plain one-time setup code is never stored; only <see cref="CodeHash"/> is persisted.
/// The real Device Token is never stored here and never placed in the protocol URI.
/// </summary>
public sealed class PrintBridgeSetupSession : BaseEntity
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Null for NewDevice sessions until the one-time-code exchange atomically creates and binds the device.
    /// Non-null for ReconnectExistingDevice sessions (bound at session creation).
    /// </summary>
    public Guid? PrintBridgeDeviceId { get; set; }
    public PrintBridgeDevice? Device { get; set; }

    /// <summary>Stored setup mode. Drives exchange behavior: device creation vs. token rotation.</summary>
    public string SetupMode { get; set; } = string.Empty;

    /// <summary>SHA-256 Base64 hash of the one-time setup code. Never store or log the raw code.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 Base64 hash of the short-lived completion credential handed to the desktop app
    /// during exchange. Used to authenticate the completion call. Never store the raw value.
    /// </summary>
    public string? CompletionCredentialHash { get; set; }

    /// <summary>Canonical Wasla Web Panel / Server URL resolved when the session was created.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ExchangedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }

    /// <summary>True when the desktop app verified the connection (ping) before reporting completion.</summary>
    public bool ConnectionVerified { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>
    /// Set only when the session moved an installation from another tenant's device (WAS-91): the device whose
    /// registration the move retired. Its record and print history stay with that tenant. A plain reference, not a
    /// foreign key: it may outlive that tenant's deletion.
    /// </summary>
    public Guid? SourcePrintBridgeDeviceId { get; set; }

    /// <summary>
    /// SHA-256 Base64 hash of the retired device's former token, kept only until the move is completed so the desktop
    /// app can repeat the identical move request after a lost response. That token no longer authenticates any device
    /// request. Never store the raw value.
    /// </summary>
    public string? SourceTokenHash { get; set; }
}
