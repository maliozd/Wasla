using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Tenant login sessions in the resolved tenant's own database (<see cref="ITenantSessionRegistry"/>). A session id is
/// 128 bits from <see cref="RandomNumberGenerator"/>. Revocation is one conditional <c>UPDATE</c> on the session's
/// primary key and user, so a repeated or concurrent logout changes nothing more and never touches another session.
/// A database failure is logged with its exception type only and surfaces as
/// <see cref="TenantSessionUnavailableException"/>; cancellation propagates.
/// </summary>
public sealed class TenantSessionRegistry : ITenantSessionRegistry
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantSessionRegistry> _logger;

    public TenantSessionRegistry(ITenantDbContextFactory dbFactory, TimeProvider time, ILogger<TenantSessionRegistry> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    public async Task<Guid> CreateAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var session = new TenantSession
        {
            Id = NewSessionId(),
            UserId = userId,
            CreatedAtUtc = _time.GetUtcNow().UtcDateTime
        };

        try
        {
            await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            db.TenantSessions.Add(session);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return session.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the exception type: no SQL, connection detail or identifier reaches a log or the error pipeline.
            _logger.LogError("Tenant session could not be created in the tenant database ({ExceptionType}).", ex.GetType().Name);
            throw new TenantSessionUnavailableException();
        }
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            var revoked = await db.TenantSessions
                .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now), ct)
                .ConfigureAwait(false);
            return revoked > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Tenant session could not be revoked in the tenant database ({ExceptionType}).", ex.GetType().Name);
            throw new TenantSessionUnavailableException();
        }
    }

    private static Guid NewSessionId()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid id;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            id = new Guid(bytes);
        }
        while (id == Guid.Empty);

        return id;
    }
}
