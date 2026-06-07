using OrderHub.Domain.Common;

namespace OrderHub.Domain.Entities.Central;

/// <summary>
/// Registered Print Bridge instance for a customer tenant. Lives in CentralDb only.
/// Authentication uses hashed token; raw token is shown once at generation time.
/// </summary>
public sealed class PrintBridgeDevice : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 Base64 hash of the Print Bridge agent token. Never store or log the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string? MachineName { get; set; }
    public string? PrinterName { get; set; }
    public string? AppVersion { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastIpAddress { get; set; }
}
