using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Tours;
using Wasla.Application.Tours;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class UserProductTourService : IUserProductTourService
{
    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly TimeProvider _time;
    private readonly ILogger<UserProductTourService> _logger;

    public UserProductTourService(
        ITenantDbContextFactory tenantDbs,
        TimeProvider time,
        ILogger<UserProductTourService> logger)
    {
        _tenantDbs = tenantDbs;
        _time = time;
        _logger = logger;
    }

    public async Task<bool> IsCompletedAsync(Guid tenantId, Guid userId, string tourKey, CancellationToken ct)
    {
        if (!ProductTourKeys.IsStorable(tourKey))
            return false;

        try
        {
            await using var db = await _tenantDbs.CreateAsync(tenantId, ct);
            return await db.UserProductTourCompletions
                .AsNoTracking()
                .AnyAsync(row => row.UserId == userId && row.TourKey == tourKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Product tour status could not be read: {ExceptionType}",
                ex.GetType().Name);
            return false;
        }
    }

    public async Task<bool> CompleteAsync(Guid tenantId, Guid userId, string tourKey, CancellationToken ct)
    {
        if (!ProductTourKeys.IsStorable(tourKey))
            return false;

        try
        {
            await using var db = await _tenantDbs.CreateAsync(tenantId, ct);
            var existing = await db.UserProductTourCompletions
                .FirstOrDefaultAsync(row => row.UserId == userId && row.TourKey == tourKey, ct);
            if (existing is not null)
                return true;

            db.UserProductTourCompletions.Add(new UserProductTourCompletion
            {
                UserId = userId,
                TourKey = tourKey,
                CompletedAtUtc = _time.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Product tour completion could not be saved: {ExceptionType}",
                ex.GetType().Name);
            return false;
        }
    }
}
