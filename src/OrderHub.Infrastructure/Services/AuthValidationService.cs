using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Auth;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

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
        var user = await db.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == emailNorm && u.IsActive, ct);

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
            user.Role);
    }
}

