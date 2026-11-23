using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The Central Admin side of a pilot application (WAS-222). Status changes and recorded contacts are single conditional
/// updates, so against a concurrent retention run either the update lands first and the row is no longer expired, or the
/// row is already gone and the administrator is told so. Every operation is limited to the pilot topic. Logs carry the row
/// id only.
/// </summary>
public sealed class CentralAdminPilotApplicationService : ICentralAdminPilotApplicationService
{
    private readonly CentralDbContext _db;
    private readonly ITurkishProvinceCatalog _provinces;
    private readonly TimeProvider _time;
    private readonly ILogger<CentralAdminPilotApplicationService> _logger;

    public CentralAdminPilotApplicationService(
        CentralDbContext db,
        ITurkishProvinceCatalog provinces,
        TimeProvider time,
        ILogger<CentralAdminPilotApplicationService> logger)
    {
        _db = db;
        _provinces = provinces;
        _time = time;
        _logger = logger;
    }

    private IQueryable<ContactMessage> Pilots => _db.ContactMessages.Where(m => m.Topic == ContactTopic.PilotApplication);

    public async Task<IReadOnlyList<PilotApplicationSearchItem>> FindAsync(string? email, string? phoneDigits, CancellationToken ct)
    {
        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        var phone = string.IsNullOrWhiteSpace(phoneDigits) ? null : phoneDigits.Trim();
        if (normalizedEmail is null && phone is null)
            return [];

        var query = Pilots.AsNoTracking();
        query = (normalizedEmail, phone) switch
        {
            ({ } e, { } p) => query.Where(m => m.Email.ToLower() == e || m.Phone == p),
            ({ } e, null) => query.Where(m => m.Email.ToLower() == e),
            _ => query.Where(m => m.Phone == phone)
        };

        var rows = await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(ICentralAdminPilotApplicationService.MaxSearchResults)
            .Select(m => new { m.Id, m.RestaurantName, m.FullName, m.CreatedAtUtc, m.PilotStatus })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new PilotApplicationSearchItem(r.Id, r.RestaurantName, r.FullName, r.CreatedAtUtc, r.PilotStatus ?? PilotApplicationStatus.New))
            .ToList();
    }

    public async Task<PilotApplicationExport?> ExportAsync(Guid id, CancellationToken ct)
    {
        var row = await Pilots.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new
            {
                m.Id,
                m.CreatedAtUtc,
                m.RestaurantName,
                m.FullName,
                m.Email,
                m.Phone,
                m.CityPlateCode,
                CityName = _db.Cities
                    .Where(c => m.CityPlateCode != null && c.CountryCode == "TR" && c.PlateCode == m.CityPlateCode)
                    .Select(c => c.Name)
                    .FirstOrDefault(),
                m.Message,
                m.Culture,
                m.PilotStatus,
                m.PilotLastActivityAtUtc,
                m.NotificationStatus,
                m.NotificationAttemptedAtUtc
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
            return null;

        var lastActivity = row.PilotLastActivityAtUtc ?? row.CreatedAtUtc;
        return new PilotApplicationExport
        {
            Id = row.Id,
            ReceivedAtUtc = row.CreatedAtUtc,
            RestaurantName = row.RestaurantName,
            ContactName = row.FullName,
            Email = row.Email,
            Phone = row.Phone ?? string.Empty,
            CityPlateCode = row.CityPlateCode ?? string.Empty,
            CityName = row.CityName,
            Note = row.Message,
            FormLanguage = row.Culture,
            Status = row.PilotStatus ?? PilotApplicationStatus.New,
            LastActivityAtUtc = lastActivity,
            DeleteOnOrAfterUtc = PilotApplicationRetention.DeleteOnOrAfter(lastActivity),
            TeamNotificationStatus = row.NotificationStatus,
            TeamNotificationAttemptedAtUtc = row.NotificationAttemptedAtUtc
        };
    }

    public async Task<PilotApplicationChangeOutcome> SetStatusAsync(Guid id, PilotApplicationStatus status, CancellationToken ct)
    {
        if (!Enum.IsDefined(status) || status == PilotApplicationStatus.New)
            return PilotApplicationChangeOutcome.Invalid;

        var now = _time.GetUtcNow().UtcDateTime;
        var changed = await Pilots
            .Where(m => m.Id == id && m.PilotStatus != status)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.PilotStatus, (PilotApplicationStatus?)status)
                    .SetProperty(m => m.PilotLastActivityAtUtc, (DateTime?)now),
                ct)
            .ConfigureAwait(false);

        if (changed > 0)
        {
            _logger.LogInformation("Pilot application {ContactMessageId}: status set to {PilotStatus}; its retention period restarts.", id, status);
            return PilotApplicationChangeOutcome.Changed;
        }

        return await ExistsAsync(id, ct).ConfigureAwait(false) ? PilotApplicationChangeOutcome.Unchanged : PilotApplicationChangeOutcome.NotFound;
    }

    public async Task<PilotApplicationChangeOutcome> RecordContactAsync(Guid id, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var changed = await Pilots
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.PilotLastActivityAtUtc, (DateTime?)now), ct)
            .ConfigureAwait(false);

        if (changed == 0)
            return PilotApplicationChangeOutcome.NotFound;

        _logger.LogInformation("Pilot application {ContactMessageId}: contact with the restaurant recorded; its retention period restarts.", id);
        return PilotApplicationChangeOutcome.Changed;
    }

    public async Task<PilotApplicationChangeOutcome> CorrectAsync(Guid id, PilotApplicationCorrection correction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correction);

        var plate = correction.CityPlateCode?.Trim() ?? string.Empty;
        var provinces = await _provinces.GetActiveProvincesAsync(ct).ConfigureAwait(false);
        if (!provinces.Any(p => p.PlateCode == plate))
            return PilotApplicationChangeOutcome.Invalid;

        var application = await Pilots.SingleOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (application is null)
            return PilotApplicationChangeOutcome.NotFound;

        try
        {
            application.CorrectPilotApplication(
                correction.RestaurantName,
                correction.ContactName,
                correction.Email,
                correction.PhoneDigits,
                plate,
                correction.Note);
        }
        catch (ArgumentException)
        {
            _db.ChangeTracker.Clear();
            return PilotApplicationChangeOutcome.Invalid;
        }

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted (by the retention cleanup or another administrator) after it was read.
            return PilotApplicationChangeOutcome.NotFound;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation("Pilot application {ContactMessageId}: personal data corrected on request.", id);
        return PilotApplicationChangeOutcome.Changed;
    }

    public async Task<PilotApplicationChangeOutcome> DeleteOnRequestAsync(Guid id, Guid adminUserId, CancellationToken ct)
    {
        if (adminUserId == Guid.Empty)
            throw new ArgumentException("A deletion on request names the administrator.", nameof(adminUserId));

        var now = _time.GetUtcNow().UtcDateTime;
        bool deleted;
        try
        {
            deleted = await PilotApplicationDestruction.TryDeleteAsync(
                _db,
                id,
                PilotApplicationRetention.DeletableOnRequestAt(now),
                DataDestructionMethod.DataSubjectRequest,
                DataDestructionActor.CentralAdmin,
                adminUserId,
                now,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Pilot application {ContactMessageId} could not be deleted on request; it is still stored. ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                id,
                ex.GetType().Name,
                PilotApplicationDestruction.SqlErrorNumber(ex));
            try
            {
                await PilotApplicationDestruction.RecordFailureAsync(
                    _db, id, DataDestructionMethod.DataSubjectRequest, DataDestructionActor.CentralAdmin, adminUserId, now, ct).ConfigureAwait(false);
            }
            catch (Exception recordEx) when (recordEx is not OperationCanceledException)
            {
                _logger.LogWarning("The failed deletion of pilot application {ContactMessageId} could not be recorded either. ExceptionType={ExceptionType}", id, recordEx.GetType().Name);
            }

            throw;
        }

        if (deleted)
        {
            _logger.LogInformation("Pilot application {ContactMessageId} deleted on a data subject's request by Central Admin user {AdminUserId}.", id, adminUserId);
            return PilotApplicationChangeOutcome.Changed;
        }

        return await ExistsAsync(id, ct).ConfigureAwait(false) ? PilotApplicationChangeOutcome.InProgress : PilotApplicationChangeOutcome.NotFound;
    }

    private Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Pilots.AsNoTracking().AnyAsync(m => m.Id == id, ct);
}
