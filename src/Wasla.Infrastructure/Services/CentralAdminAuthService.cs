using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class CentralAdminAuthService : ICentralAdminAuthService
{
    private readonly CentralDbContext _db;
    private readonly ILogger<CentralAdminAuthService> _logger;

    public CentralAdminAuthService(CentralDbContext db, ILogger<CentralAdminAuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new CentralAdminLoginResult(false, null, null, null, "Admin.InvalidCredentials");

        var normalized = email.Trim().ToUpperInvariant();

        var user = await _db.CentralAdminUsers
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct)
            .ConfigureAwait(false);

        if (user is null)
            return new CentralAdminLoginResult(false, null, null, null, "Admin.InvalidCredentials");

        if (!user.IsActive)
            return new CentralAdminLoginResult(false, user.Id, user.Email, user.DisplayName, "Admin.AccountInactive");

        try
        {
            var ok = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
            if (!ok)
                return new CentralAdminLoginResult(false, null, null, null, "Admin.InvalidCredentials");

            var now = DateTime.UtcNow;
            user.LastLoginAt = now;
            user.UpdatedAt = now;
            // A row inserted outside EF can carry the column's empty default. Sessions are never issued with it.
            if (user.SecurityStamp == Guid.Empty)
                user.SecurityStamp = Guid.NewGuid();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new CentralAdminLoginResult(true, user.Id, user.Email, user.DisplayName, null, user.SecurityStamp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Central admin password verification failed.");
            return new CentralAdminLoginResult(false, null, null, null, "Admin.InvalidCredentials");
        }
    }
}
