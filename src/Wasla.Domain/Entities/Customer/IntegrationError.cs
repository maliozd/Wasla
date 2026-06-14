using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// Records an individual integration failure for later investigation.
/// Separate from SyncLog because one sync cycle can produce many errors
/// (or none) and we want to be able to mark each one resolved independently.
/// </summary>
public class IntegrationError : BaseEntity
{
    public FoodPlatform Platform { get; set; }
    public Guid? PlatformConnectionId { get; set; }

    /// <summary>
    /// Short category, e.g. "Timeout", "Unauthorized", "UpsertFailed".
    /// Good candidate for grouping in dashboards.
    /// </summary>
    public string ErrorType { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable message. Must be sanitized - no secrets, no full stack
    /// traces with sensitive data.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    public bool IsResolved { get; set; }
}
