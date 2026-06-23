using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Registered Print Bridge instance for a tenant. Lives in CentralDb only.
/// Authentication uses hashed token; raw token is shown once at generation time.
/// </summary>
public sealed class PrintBridgeDevice : BaseEntity
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 Base64 hash of the Print Bridge agent token. Never store or log the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string? MachineName { get; set; }
    public string? PrinterName { get; set; }
    public string? AppVersion { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastIpAddress { get; set; }

    /// <summary>UTC timestamp when the device was retired from normal management.</summary>
    public DateTime? RemovedAtUtc { get; set; }
}
