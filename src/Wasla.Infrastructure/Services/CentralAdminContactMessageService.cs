using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class CentralAdminContactMessageService : ICentralAdminContactMessageService
{
    private readonly CentralDbContext _db;
    private readonly TimeProvider _time;

    public CentralAdminContactMessageService(CentralDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <remarks>
    /// Newest first, with the id as a tie-breaker so pages are stable. The list projects only the columns it shows; the
    /// sender's email, phone and message are read by the detail page only.
    /// </remarks>
    public async Task<AdminContactMessageListResult> GetListAsync(int page, int pageSize, AdminContactMessageFilter filter, CancellationToken ct)
    {
        var size = AdminContactMessagePaging.ClampPageSize(pageSize);
        var query = _db.ContactMessages.AsNoTracking();
        query = filter switch
        {
            AdminContactMessageFilter.PilotApplications => query.Where(m => m.Topic == ContactTopic.PilotApplication),
            AdminContactMessageFilter.ContactMessages => query.Where(m => m.Topic != ContactTopic.PilotApplication),
            _ => query
        };

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var lastPage = Math.Max(1, (total + size - 1) / size);
        var current = Math.Clamp(page, 1, lastPage);

        var items = total == 0
            ? []
            : await query
                .OrderByDescending(m => m.CreatedAtUtc)
                .ThenByDescending(m => m.Id)
                .Skip((current - 1) * size)
                .Take(size)
                .Select(m => new AdminContactMessageListItemDto
                {
                    Id = m.Id,
                    FullName = m.FullName,
                    RestaurantName = m.RestaurantName,
                    Topic = m.Topic,
                    NotificationStatus = m.NotificationStatus,
                    CreatedAtUtc = m.CreatedAtUtc
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return new AdminContactMessageListResult
        {
            Items = items,
            Page = current,
            PageSize = size,
            TotalCount = total
        };
    }

    public async Task<AdminContactMessageDetailDto?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.ContactMessages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new
            {
                m.Id,
                m.FullName,
                m.RestaurantName,
                m.Email,
                m.Phone,
                m.Topic,
                m.Message,
                m.CityPlateCode,
                CityName = _db.Cities
                    .Where(c => m.CityPlateCode != null && c.CountryCode == "TR" && c.PlateCode == m.CityPlateCode)
                    .Select(c => c.Name)
                    .FirstOrDefault(),
                m.Culture,
                m.CreatedAtUtc,
                m.NotificationStatus,
                m.NotificationAttemptedAtUtc
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
            return null;

        return new AdminContactMessageDetailDto
        {
            Id = row.Id,
            FullName = row.FullName,
            RestaurantName = row.RestaurantName,
            Email = row.Email,
            Phone = row.Phone,
            Topic = row.Topic,
            Message = row.Message,
            CityPlateCode = row.CityPlateCode,
            CityName = row.CityName,
            Culture = row.Culture,
            CreatedAtUtc = row.CreatedAtUtc,
            NotificationStatus = row.NotificationStatus,
            NotificationAttemptedAtUtc = row.NotificationAttemptedAtUtc,
            CanResendNotification = ContactNotificationRetry.CanResend(
                row.NotificationStatus, row.CreatedAtUtc, row.NotificationAttemptedAtUtc, _time.GetUtcNow().UtcDateTime)
        };
    }
}
