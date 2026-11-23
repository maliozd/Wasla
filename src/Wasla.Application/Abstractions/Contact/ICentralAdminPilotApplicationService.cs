using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Contact;

/// <summary>How a Central Admin change to a pilot application ended.</summary>
public enum PilotApplicationChangeOutcome
{
    Changed,

    /// <summary>Nothing to change: the application already had that status.</summary>
    Unchanged,

    /// <summary>No pilot application has this id (never stored, a contact message, or already deleted).</summary>
    NotFound,

    /// <summary>The corrected values break the application's rules (for example a city outside the catalog).</summary>
    Invalid,

    /// <summary>A team notification attempt for it is running; try again in a few minutes.</summary>
    InProgress
}

/// <summary>A pilot application found for a data subject's request: id and codes, so the list shows who without the content.</summary>
public sealed record PilotApplicationSearchItem(
    Guid Id,
    string RestaurantName,
    string ContactName,
    DateTime CreatedAtUtc,
    PilotApplicationStatus Status);

/// <summary>Corrected personal data of a pilot application, as the data subject asked for it.</summary>
public sealed record PilotApplicationCorrection(
    string RestaurantName,
    string ContactName,
    string Email,
    string PhoneDigits,
    string CityPlateCode,
    string? Note);

/// <summary>Everything stored about one pilot application, for a data subject's access request.</summary>
public sealed class PilotApplicationExport
{
    public Guid Id { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
    public string RestaurantName { get; init; } = string.Empty;
    public string ContactName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;

    /// <summary>The ten national digits, without the country code.</summary>
    public string Phone { get; init; } = string.Empty;

    public string CityPlateCode { get; init; } = string.Empty;
    public string? CityName { get; init; }
    public string Note { get; init; } = string.Empty;
    public string? FormLanguage { get; init; }
    public PilotApplicationStatus Status { get; init; }
    public DateTime LastActivityAtUtc { get; init; }
    public DateTime DeleteOnOrAfterUtc { get; init; }
    public ContactMessageNotificationStatus TeamNotificationStatus { get; init; }
    public DateTime? TeamNotificationAttemptedAtUtc { get; init; }
}

/// <summary>
/// The Central Admin side of a pilot application (WAS-222): the team's status and recorded contacts, which move the
/// retention period, and the data subject request procedure (find, export, correct, delete). Identity verification is a
/// manual step before export, correction and deletion; nothing here sends the data anywhere. Contact form messages are
/// never found, changed or deleted by these operations.
/// </summary>
public interface ICentralAdminPilotApplicationService
{
    public const int MaxSearchResults = 20;

    /// <summary>
    /// The pilot applications whose email address equals <paramref name="email"/> (ignoring case) or whose phone number
    /// equals <paramref name="phoneDigits"/> (ten digits), newest first, at most <see cref="MaxSearchResults"/>. Exact
    /// matches only, so the search cannot list applications by a fragment.
    /// </summary>
    Task<IReadOnlyList<PilotApplicationSearchItem>> FindAsync(string? email, string? phoneDigits, CancellationToken ct);

    /// <summary>The stored data of one application, or null. The caller logs the export with ids only.</summary>
    Task<PilotApplicationExport?> ExportAsync(Guid id, CancellationToken ct);

    /// <summary>Sets the team's status; a change moves the last activity to now and so restarts the retention period.</summary>
    Task<PilotApplicationChangeOutcome> SetStatusAsync(Guid id, PilotApplicationStatus status, CancellationToken ct);

    /// <summary>Records that the team was in contact with the restaurant now, which restarts the retention period.</summary>
    Task<PilotApplicationChangeOutcome> RecordContactAsync(Guid id, CancellationToken ct);

    /// <summary>Corrects the personal data. A correction is not contact and does not restart the retention period.</summary>
    Task<PilotApplicationChangeOutcome> CorrectAsync(Guid id, PilotApplicationCorrection correction, CancellationToken ct);

    /// <summary>
    /// Deletes the application on the data subject's request, with a destruction record naming
    /// <paramref name="adminUserId"/>. Refused with <see cref="PilotApplicationChangeOutcome.InProgress"/> while a team
    /// notification attempt is running.
    /// </summary>
    Task<PilotApplicationChangeOutcome> DeleteOnRequestAsync(Guid id, Guid adminUserId, CancellationToken ct);
}
