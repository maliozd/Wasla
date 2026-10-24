using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Security;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Tenant session validation for Wasla.Web and Wasla.Api. Claims and the tenant match are checked first, so a cookie
/// that is malformed or belongs to another tenant never opens a database. Then one primary-key read of the resolved
/// tenant's database, projecting only the account state. It opens only the database of the tenant it is given and
/// caches nothing.
/// </summary>
public sealed class TenantSessionValidator : ITenantSessionValidator
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly ILogger<TenantSessionValidator> _logger;

    public TenantSessionValidator(ITenantDbContextFactory dbFactory, ILogger<TenantSessionValidator> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<TenantSessionState> ValidateAsync(Guid? resolvedTenantId, ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (resolvedTenantId is not { } tenantId || tenantId == Guid.Empty)
            return TenantSessionState.NoResolvedTenant;

        if (!TenantSessionClaims.TryRead(principal, out var session))
            return TenantSessionState.MissingOrMalformedClaims;

        // Compared before any database is opened: a cookie from another tenant never reaches this tenant's database.
        if (session.TenantId != tenantId)
            return TenantSessionState.TenantMismatch;

        try
        {
            return await ReadStateAsync(tenantId, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed. Only the exception type is logged, and the original exception is not passed on, so no SQL or
            // connection detail reaches a log, a response or the error pipeline.
            _logger.LogError("Tenant session could not be validated against the tenant database ({ExceptionType}).", ex.GetType().Name);
            throw new TenantSessionUnavailableException();
        }
    }

    private async Task<TenantSessionState> ReadStateAsync(Guid tenantId, TenantSessionClaims session, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        var user = await db.AppUsers
            .AsNoTracking()
            .Where(u => u.Id == session.UserId)
            .Select(u => new { u.IsActive, u.Role, u.SecurityStamp })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (user is null)
            return TenantSessionState.UserNotFound;

        if (!user.IsActive)
            return TenantSessionState.UserInactive;

        if (user.SecurityStamp == Guid.Empty || user.SecurityStamp != session.SecurityStamp)
            return TenantSessionState.StampChanged;

        // A role edited without a new stamp (for example directly in the database) is not trusted either.
        if (user.Role != session.Role)
            return TenantSessionState.RoleChanged;

        return TenantSessionState.Valid;
    }
}
