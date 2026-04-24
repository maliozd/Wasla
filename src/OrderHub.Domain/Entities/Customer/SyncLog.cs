using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Customer;

/// <summary>
/// One row per sync attempt per PlatformConnection. Critical for debugging
/// "why didn't my orders show up?" support tickets.
/// </summary>
public class SyncLog : BaseEntity
{
    public Guid PlatformConnectionId { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public SyncStatus Status { get; set; } = SyncStatus.Running;

    public int OrdersFetched { get; set; }
    public int OrdersInserted { get; set; }
    public int OrdersUpdated { get; set; }

    /// <summary>
    /// Error message if the sync failed. Sanitized - no secrets.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
