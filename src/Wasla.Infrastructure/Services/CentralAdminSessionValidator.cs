using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Security;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Central Admin session validation. Claims are checked first, so a malformed or pre-stamp cookie never reaches
/// CentralDb. Then one primary-key read of <c>CentralAdminUsers</c>, projecting only the account state. It opens no tenant
/// database and caches nothing.
/// </summary>
public sealed class CentralAdminSessionValidator : ICentralAdminSessionValidator
{
    private readonly CentralDbContext _db;
    private readonly ILogger<CentralAdminSessionValidator> _logger;

    public CentralAdminSessionValidator(CentralDbContext db, ILogger<CentralAdminSessionValidator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<CentralAdminSessionState> ValidateAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (!CentralAdminSessionClaims.TryRead(principal, out var session))
            return CentralAdminSessionState.MissingOrMalformedClaims;

        try
        {
            return await ReadStateAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed. Only the exception type is logged, and the original exception is not passed on, so no SQL or
            // connection detail reaches a log, a response or the error pipeline.
            _logger.LogError("Central admin session could not be validated against CentralDb ({ExceptionType}).", ex.GetType().Name);
            throw new CentralAdminSessionUnavailableException();
        }
    }

    private async Task<CentralAdminSessionState> ReadStateAsync(CentralAdminSessionClaims session, CancellationToken ct)
    {
        var account = await _db.CentralAdminUsers
            .AsNoTracking()
            .Where(x => x.Id == session.AdminUserId)
            .Select(x => new { x.IsActive, x.SecurityStamp })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (account is null)
            return CentralAdminSessionState.AccountNotFound;

        if (!account.IsActive)
            return CentralAdminSessionState.AccountInactive;

        if (account.SecurityStamp == Guid.Empty || account.SecurityStamp != session.SecurityStamp)
            return CentralAdminSessionState.StampChanged;

        return CentralAdminSessionState.Valid;
    }
}
