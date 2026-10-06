using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Stores the login time on the signed-in user in that tenant's database. A single conditional UPDATE only
/// moves the value forward, so a slower concurrent login cannot replace a newer time. It does not touch
/// UpdatedAt. A database failure here (for example a tenant not yet migrated) is logged with safe
/// identifiers only and does not fail the login; cancellation and other errors propagate.
/// </summary>
public sealed class TenantLoginRecorder : ITenantLoginRecorder
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantLoginRecorder> _logger;

    public TenantLoginRecorder(
        ITenantDbContextFactory dbFactory,
        TimeProvider time,
        ILogger<TenantLoginRecorder> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    public async Task RecordSuccessfulLoginAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            await db.AppUsers
                .Where(u => u.Id == userId && (u.LastLoginAt == null || u.LastLoginAt < now))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, now), ct)
                .ConfigureAwait(false);
        }
        catch (DbException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Last login could not be recorded. TenantId={TenantId} UserId={UserId} FailureType={FailureType}",
                tenantId,
                userId,
                ex.GetType().Name);
        }
    }
}
