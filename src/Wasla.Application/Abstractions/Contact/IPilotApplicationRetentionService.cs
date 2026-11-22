using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Contact;

/// <summary>One run of the pilot application retention cleanup (WAS-222).</summary>
/// <param name="DryRun">Report what would be deleted and change nothing.</param>
/// <param name="BatchSize">How many rows one round reads, from 1 to <see cref="PilotRetentionRun.MaxBatchSize"/>.</param>
public sealed record PilotRetentionRunRequest(bool DryRun, int BatchSize = PilotRetentionRun.DefaultBatchSize);

public static class PilotRetentionRun
{
    public const int DefaultBatchSize = 100;
    public const int MaxBatchSize = 1000;
}

/// <summary>An expired pilot application, by id and codes only: no name, contact, city or note.</summary>
public sealed record PilotRetentionCandidate(Guid Id, PilotApplicationStatus Status, DateTime LastActivityAtUtc, DateTime DeleteOnOrAfterUtc);

/// <summary>What one run found and did. Holds identifiers and counts only, so it may be printed and logged.</summary>
public sealed class PilotRetentionRunResult
{
    public DateTime NowUtc { get; init; }

    public bool DryRun { get; init; }

    /// <summary>The expired applications found: in a dry run the ones that would be deleted, otherwise the ones tried.</summary>
    public IReadOnlyList<PilotRetentionCandidate> Expired { get; init; } = [];

    /// <summary>The applications this run deleted, each with a destruction record.</summary>
    public IReadOnlyList<Guid> Deleted { get; init; } = [];

    /// <summary>
    /// Expired when read but not deleted, because by the time of the delete another run had deleted it, the team had
    /// recorded new activity, or a notification attempt had started. Nothing is wrong; the row is no longer expired.
    /// </summary>
    public IReadOnlyList<Guid> NoLongerExpired { get; init; } = [];

    /// <summary>The applications whose deletion failed. They are still stored and the next run tries again.</summary>
    public IReadOnlyList<Guid> Failed { get; init; } = [];

    /// <summary>Destruction records older than their three-year retention: removed, or in a dry run found.</summary>
    public int DestructionRecordsPastRetention { get; init; }

    public bool Succeeded => Failed.Count == 0;
}

/// <summary>
/// Deletes pilot applications whose retention period has ended (<see cref="Wasla.Application.Contact.PilotApplicationRetention"/>),
/// in bounded batches, each with a destruction record, and removes destruction records past their own retention. Safe to
/// repeat and to run twice at once: every delete is conditional on the rule at that moment, so a row is deleted and
/// recorded once. Run by the <c>pilot-applications-retention</c> CLI command.
/// </summary>
public interface IPilotApplicationRetentionService
{
    Task<PilotRetentionRunResult> RunAsync(PilotRetentionRunRequest request, CancellationToken ct);
}
