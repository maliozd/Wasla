using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Orders;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;

namespace Wasla.Infrastructure.Services;

public sealed class UserOrderActionPreferenceService : IUserOrderActionPreferenceService
{
    private const bool DefaultConfirmRoutineActions = true;

    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly TimeProvider _time;

    public UserOrderActionPreferenceService(ITenantDbContextFactory tenantDbs, TimeProvider time)
    {
        _tenantDbs = tenantDbs;
        _time = time;
    }

    public async Task<bool> GetConfirmRoutineActionsAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        return await ReadAsync(db, userId, ct).ConfigureAwait(false) ?? DefaultConfirmRoutineActions;
    }

    public async Task<bool> SetConfirmRoutineActionsAsync(Guid tenantId, Guid userId, bool confirmRoutineActions, CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;

        if (await UpdateAsync(db, userId, confirmRoutineActions, now, ct).ConfigureAwait(false) == 0)
        {
            db.UserOrderActionPreferences.Add(new UserOrderActionPreference
            {
                UserId = userId,
                ConfirmRoutineActions = confirmRoutineActions,
                CreatedAt = now,
                UpdatedAt = now
            });

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsFirstWriteConflict(ex))
            {
                // Two tabs saved the first choice at once: the row exists now, so the later choice updates it.
                db.ChangeTracker.Clear();
                await UpdateAsync(db, userId, confirmRoutineActions, now, ct).ConfigureAwait(false);
            }
        }

        return await ReadAsync(db, userId, ct).ConfigureAwait(false) ?? DefaultConfirmRoutineActions;
    }

    private static Task<bool?> ReadAsync(TenantDbContext db, Guid userId, CancellationToken ct) =>
        db.UserOrderActionPreferences
            .AsNoTracking()
            .Where(row => row.UserId == userId)
            .Select(row => (bool?)row.ConfirmRoutineActions)
            .FirstOrDefaultAsync(ct);

    private static Task<int> UpdateAsync(TenantDbContext db, Guid userId, bool confirmRoutineActions, DateTime now, CancellationToken ct) =>
        db.UserOrderActionPreferences
            .Where(row => row.UserId == userId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(row => row.ConfirmRoutineActions, confirmRoutineActions)
                .SetProperty(row => row.UpdatedAt, now), ct);

    private static bool IsFirstWriteConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(UserOrderActionPreferenceIndexes.User, StringComparison.Ordinal)
                || current.Message.Contains("UNIQUE constraint failed: UserOrderActionPreferences.UserId", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
