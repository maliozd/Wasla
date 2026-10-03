using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.GuidedSetup;

/// <summary>
/// The current tenant user's guided-setup choice and position. State is per user inside the
/// tenant database; nothing here marks another user or operational setup (platforms, Print Bridge) complete.
/// The one tenant-level effect: <see cref="SkipAsync"/> and <see cref="CompleteAsync"/> also move the tenant's
/// operational mode from Setup to Live, atomically with the user's change (see <c>TenantOperationalMode</c>).
/// Callers pass the tenant and user resolved server-side for the current request, after their own authorization.
/// </summary>
public interface IGuidedSetupService
{
    /// <summary>
    /// Reads the user's state without writing anything. A user without a guided-setup row is
    /// NotStarted; earlier product-tour completions do not count.
    /// </summary>
    Task<GuidedSetupState> GetAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// NotStarted → InProgress at <paramref name="position"/>. Starting again keeps the saved position. The tenant's
    /// operational mode is not touched.
    /// </summary>
    Task<GuidedSetupResult> StartAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct);

    /// <summary>
    /// Saves the position while InProgress. Pausing is the same call: the status stays InProgress
    /// and the saved position is where the user resumes.
    /// </summary>
    Task<GuidedSetupResult> SaveProgressAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct);

    /// <summary>NotStarted or InProgress → Skipped, and the tenant Setup → Live. Never overwrites Completed.</summary>
    Task<GuidedSetupResult> SkipAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>InProgress → Completed, and the tenant Setup → Live. Never overwrites Skipped.</summary>
    Task<GuidedSetupResult> CompleteAsync(Guid tenantId, Guid userId, CancellationToken ct);
}

/// <summary>A stable section key from <see cref="GuidedSetupSections"/> and an optional step key.</summary>
public sealed record GuidedSetupPosition(string SectionKey, string? StepKey = null)
{
    public bool IsValid =>
        GuidedSetupSections.IsKnown(SectionKey) && GuidedSetupSections.IsValidStepKey(StepKey);
}

public sealed record GuidedSetupState(
    GuidedSetupStatus Status,
    string? SectionKey,
    string? StepKey,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? SkippedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public static GuidedSetupState NotStarted { get; } =
        new(GuidedSetupStatus.NotStarted, null, null, null, null, null, null);

    public bool IsTerminal => GuidedSetupTransitions.IsTerminal(Status);
}

public sealed record GuidedSetupResult(GuidedSetupOutcome Outcome, GuidedSetupState State)
{
    /// <summary>True when the user is now in the requested state, whether or not this call wrote it.</summary>
    public bool Succeeded => Outcome is GuidedSetupOutcome.Applied or GuidedSetupOutcome.Unchanged;
}
