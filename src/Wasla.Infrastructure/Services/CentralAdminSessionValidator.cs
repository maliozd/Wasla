using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Admin;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// One CentralDb read per validated request, projecting only the account state. No tenant database is opened and
/// nothing is cached.
/// </summary>
public sealed class CentralAdminSessionValidator : ICentralAdminSessionValidator
{
    private readonly CentralDbContext _db;

    public CentralAdminSessionValidator(CentralDbContext db)
    {
        _db = db;
    }

    public async Task<CentralAdminSessionState> ValidateAsync(Guid adminUserId, Guid securityStamp, CancellationToken ct)
    {
        var account = await _db.CentralAdminUsers
            .AsNoTracking()
            .Where(x => x.Id == adminUserId)
            .Select(x => new { x.IsActive, x.SecurityStamp })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (account is null)
            return CentralAdminSessionState.AccountNotFound;

        if (!account.IsActive)
            return CentralAdminSessionState.AccountInactive;

        if (account.SecurityStamp == Guid.Empty || account.SecurityStamp != securityStamp)
            return CentralAdminSessionState.StampChanged;

        return CentralAdminSessionState.Valid;
    }
}
