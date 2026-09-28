using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// A user-scoped practice order. It is not an <see cref="Order"/> and is never synced or printed.
/// </summary>
public sealed class GuidedDemoSession : BaseEntity
{
    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public string ScenarioCode { get; set; } = string.Empty;

    public OrderStatus Status { get; set; } = OrderStatus.New;

    public string CustomerNameKey { get; set; } = string.Empty;

    public string? NoteKey { get; set; }

    public string ItemsJson { get; set; } = "[]";

    public DateTime ReceivedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
