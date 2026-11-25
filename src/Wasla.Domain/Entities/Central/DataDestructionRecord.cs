namespace Wasla.Domain.Entities.Central;

/// <summary>What kind of record a destruction operation removed.</summary>
public enum DataDestructionSubject
{
    /// <summary>A pilot application (<see cref="ContactMessage"/> with the pilot topic).</summary>
    PilotApplication = 1
}

/// <summary>Why the record was destroyed.</summary>
public enum DataDestructionMethod
{
    /// <summary>The retention period ended and the scheduled cleanup deleted it.</summary>
    RetentionExpired = 1,

    /// <summary>A Central Admin deleted it on a data subject's request, after verifying the person's identity by hand.</summary>
    DataSubjectRequest = 2
}

/// <summary>How the operation ended.</summary>
public enum DataDestructionResult
{
    Deleted = 1,

    /// <summary>The deletion failed and the record is still stored; a later run tries again.</summary>
    Failed = 2
}

/// <summary>Who ran the operation.</summary>
public enum DataDestructionActor
{
    /// <summary>The <c>pilot-applications-retention</c> CLI command.</summary>
    RetentionJob = 1,

    /// <summary>A Central Admin user, named by <see cref="DataDestructionRecord.ActorUserId"/>.</summary>
    CentralAdmin = 2
}

/// <summary>
/// The record of one destruction operation on personal data (WAS-222): which record, when, how, with what result and by
/// whom. It holds identifiers and codes only, never the destroyed content (no name, email, phone, note or city), so it
/// is not a copy of the data it records the deletion of. It is kept for <see cref="RetentionYears"/> years and then
/// removed by the same cleanup.
/// </summary>
public sealed class DataDestructionRecord
{
    /// <summary>How long a destruction record is kept: the three years required for destruction operation records.</summary>
    public const int RetentionYears = 3;

    private DataDestructionRecord()
    {
    }

    public Guid Id { get; private set; }

    public DataDestructionSubject Subject { get; private set; }

    /// <summary>The id the destroyed record had.</summary>
    public Guid SubjectId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public DataDestructionMethod Method { get; private set; }

    public DataDestructionResult Result { get; private set; }

    public DataDestructionActor Actor { get; private set; }

    /// <summary>The Central Admin user's id when <see cref="Actor"/> is <see cref="DataDestructionActor.CentralAdmin"/>; otherwise null.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>When this record itself may be removed: <see cref="RetentionYears"/> years after <see cref="OccurredAtUtc"/>.</summary>
    public DateTime RetainUntilUtc { get; private set; }

    public static DataDestructionRecord Create(
        DataDestructionSubject subject,
        Guid subjectId,
        DataDestructionMethod method,
        DataDestructionResult result,
        DataDestructionActor actor,
        Guid? actorUserId,
        DateTime nowUtc)
    {
        if (subjectId == Guid.Empty)
            throw new ArgumentException("A destruction record names the destroyed record.", nameof(subjectId));
        if (!Enum.IsDefined(subject) || !Enum.IsDefined(method) || !Enum.IsDefined(result) || !Enum.IsDefined(actor))
            throw new ArgumentOutOfRangeException(nameof(subject), "Unknown destruction record code.");
        if (actor == DataDestructionActor.CentralAdmin && (actorUserId is null || actorUserId == Guid.Empty))
            throw new ArgumentException("A Central Admin destruction names the administrator.", nameof(actorUserId));
        if (actor != DataDestructionActor.CentralAdmin && actorUserId is not null)
            throw new ArgumentException("Only a Central Admin destruction names a user.", nameof(actorUserId));
        if (nowUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException("Destruction times are UTC.", nameof(nowUtc));

        return new DataDestructionRecord
        {
            Id = Guid.NewGuid(),
            Subject = subject,
            SubjectId = subjectId,
            OccurredAtUtc = nowUtc,
            Method = method,
            Result = result,
            Actor = actor,
            ActorUserId = actorUserId,
            RetainUntilUtc = nowUtc.AddYears(RetentionYears)
        };
    }
}
