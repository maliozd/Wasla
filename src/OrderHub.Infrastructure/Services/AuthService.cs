using System.Security.Claims;
using BCrypt.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Application.Auth.Services;
using OrderHub.Infrastructure.Persistence.Tenant;

namespace OrderHub.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-never-matches");

    private readonly ITenantDbContextFactory _customerDbFactory;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthService(
        ITenantDbContextFactory customerDbFactory,
        ICurrentTenantService currentTenantService,
        IHttpContextAccessor httpContextAccessor)
    {
        _customerDbFactory = customerDbFactory;
        _currentTenantService = currentTenantService;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<bool> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (string.IsNullOrEmpty(password)) return false;

        var tenant = _currentTenantService.CurrentTenant;
        if (tenant is null) return false;

        await using var db = await _customerDbFactory.CreateAsync(tenant.Id, ct).ConfigureAwait(false);

        var user = await db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email, ct)
            .ConfigureAwait(false);

        var hashToVerify = user?.PasswordHash ?? DummyHash;
        var verified = BCrypt.Net.BCrypt.Verify(password, hashToVerify);
        if (user is null || !user.IsActive || !verified) return false;

        var claims = new List<Claim>
        {
            new("CustomerId", tenant.Id.ToString()),
            new("UserId", user.Id.ToString()),
            new("Role", user.Role.ToString()),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(ClaimTypes.Email, user.Email),
            new("Email", user.Email),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null) return false;

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);

        return true;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null) return;

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}

