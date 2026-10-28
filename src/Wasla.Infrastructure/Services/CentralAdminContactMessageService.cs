using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Contact;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class CentralAdminContactMessageService : ICentralAdminContactMessageService
{
    private readonly CentralDbContext _db;

    public CentralAdminContactMessageService(CentralDbContext db)
    {
        _db = db;
    }

    /// <remarks>
    /// Newest first, with the id as a tie-breaker so pages are stable. The list projects only the columns it shows; the
    /// sender's email, phone and message are read by the detail page only.
    /// </remarks>
    public async Task<AdminContactMessageListResult> GetListAsync(int page, int pageSize, CancellationToken ct)
    {
        var size = AdminContactMessagePaging.ClampPageSize(pageSize);
        var total = await _db.ContactMessages.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
        var lastPage = Math.Max(1, (total + size - 1) / size);
        var current = Math.Clamp(page, 1, lastPage);

        var items = total == 0
            ? []
            : await _db.ContactMessages
                .AsNoTracking()
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

    public async Task<AdminContactMessageDetailDto?> GetDetailAsync(Guid id, CancellationToken ct) =>
        await _db.ContactMessages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new AdminContactMessageDetailDto
            {
                Id = m.Id,
                FullName = m.FullName,
                RestaurantName = m.RestaurantName,
                Email = m.Email,
                Phone = m.Phone,
                Topic = m.Topic,
                Message = m.Message,
                Culture = m.Culture,
                CreatedAtUtc = m.CreatedAtUtc,
                NotificationStatus = m.NotificationStatus,
                NotificationAttemptedAtUtc = m.NotificationAttemptedAtUtc
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
