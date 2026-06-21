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

    public Guid PrintBridgeDeviceId { get; set; }
    public PrintBridgeDevice? Device { get; set; }

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
}
