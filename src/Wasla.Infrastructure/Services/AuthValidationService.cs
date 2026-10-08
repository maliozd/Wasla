using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class AuthValidationService : IAuthValidationService
{
    private readonly ITenantDbContextFactory _dbFactory;

    public AuthValidationService(ITenantDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AuthSessionResult?> ValidateAsync(Guid customerId, string email, string password, CancellationToken ct)
    {
        if (customerId == Guid.Empty) return null;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return null;

        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var emailNorm = email.Trim();
        // Only the columns authentication needs, including the security stamp every tenant session is validated
        // against (so a tenant database without that column cannot sign in). Migrations must run before a new Web
        // version starts.
        // This is a credential check only; the Web login records LastLoginAt after sign-in (ITenantLoginRecorder).
        var user = await db.AppUsers.AsNoTracking()
            .Where(u => u.Email == emailNorm && u.IsActive)
            .Select(u => new { u.Id, u.Email, u.FullName, u.Role, u.PasswordHash, u.SecurityStamp })
            .FirstOrDefaultAsync(ct);

        // Timing-attack resistance: always verify against a real hash
        var dummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password");
        var hash = user?.PasswordHash ?? dummyHash;
        var ok = BCrypt.Net.BCrypt.Verify(password, hash);

        if (!ok || user is null) return null;

        return new AuthSessionResult(
            customerId,
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            user.SecurityStamp);
    }

    public async Task<AuthSessionResult?> GetActiveSessionAsync(Guid customerId, Guid userId, CancellationToken ct)
    {
        if (customerId == Guid.Empty || userId == Guid.Empty) return null;

        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var user = await db.AppUsers.AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Id, u.Email, u.FullName, u.Role, u.SecurityStamp })
            .FirstOrDefaultAsync(ct);

        return user is null
            ? null
            : new AuthSessionResult(customerId, user.Id, user.Email, user.FullName, user.Role, user.SecurityStamp);
    }
}
