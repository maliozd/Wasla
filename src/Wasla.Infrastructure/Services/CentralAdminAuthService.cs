using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class CentralAdminAuthService : ICentralAdminAuthService
{
    // The one failure every unsuccessful attempt gets: an unknown email, an inactive account and a wrong password
    // cannot be told apart from the outside.
    private static readonly CentralAdminLoginResult Failed = new(false, null, null, null, "Admin.InvalidCredentials", null);

    private readonly CentralDbContext _db;
    private readonly ILogger<CentralAdminAuthService> _logger;
    private readonly Func<string, string, bool> _verifyPassword;

    public CentralAdminAuthService(CentralDbContext db, ILogger<CentralAdminAuthService> logger)
        : this(db, logger, (password, hash) => BCrypt.Net.BCrypt.Verify(password, hash))
    {
    }

    /// <summary>For tests: observes which hash each attempt is verified against.</summary>
    internal CentralAdminAuthService(
        CentralDbContext db,
        ILogger<CentralAdminAuthService> logger,
        Func<string, string, bool> verifyPassword)
    {
        _db = db;
        _logger = logger;
        _verifyPassword = verifyPassword;
    }

    public async Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Failed;

        var normalized = email.Trim().ToUpperInvariant();

        var user = await _db.CentralAdminUsers
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct)
            .ConfigureAwait(false);

        try
        {
            // Every attempt verifies one hash: the stored one, or the tenant login's precomputed dummy hash (same
            // default work factor) for an unknown email, so an unknown email does not answer faster. An inactive
            // account is checked against its own hash and then refused like a wrong password.
            var passwordMatches = _verifyPassword(password, user?.PasswordHash ?? AuthValidationService.DummyHash);
            if (user is null || !user.IsActive || !passwordMatches)
                return Failed;

            var now = DateTime.UtcNow;
            user.LastLoginAt = now;
            user.UpdatedAt = now;
            // A row inserted outside EF can carry the column's empty default, which no session is accepted with.
            if (user.SecurityStamp == Guid.Empty)
                user.SecurityStamp = Guid.NewGuid();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // The stamp read together with the verified hash: if the password changes after this read, the session is
            // issued with the old stamp and rejected on its first request.
            return new CentralAdminLoginResult(true, user.Id, user.Email, user.DisplayName, null, user.SecurityStamp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Central admin password verification failed.");
            return Failed;
        }
    }
}
