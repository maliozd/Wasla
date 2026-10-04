using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// One tenant user's guided-setup progress (at most one row per user).
/// This is the user's choice and position in the guidance, not tenant operational readiness:
/// it never records that a platform, Print Bridge or the restaurant profile is set up.
/// <see cref="BaseEntity.UpdatedAt"/> records the last state or position change.
/// </summary>
public sealed class UserGuidedSetupState : BaseEntity
{
    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public GuidedSetupStatus Status { get; set; } = GuidedSetupStatus.InProgress;

    /// <summary>Stable section key, for example <c>platform-connections</c>.</summary>
    public string? CurrentSectionKey { get; set; }

    /// <summary>Stable step key within the section, when the section has steps.</summary>
    public string? CurrentStepKey { get; set; }

    /// <summary>Set when the user started. Null only when the user skipped before starting.</summary>
    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime? SkippedAtUtc { get; set; }
}
