using System.Security.Claims;
using BCrypt.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Persistence;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Application.Auth.Services;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private readonly ICustomerDbContextFactory _customerDbFactory;
    private readonly ICurrentCustomerService _currentCustomerService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthService(
        ICustomerDbContextFactory customerDbFactory,
        ICurrentCustomerService currentCustomerService,
        IHttpContextAccessor httpContextAccessor)
    {
        _customerDbFactory = customerDbFactory;
        _currentCustomerService = currentCustomerService;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<bool> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (string.IsNullOrEmpty(password)) return false;

        var customer = _currentCustomerService.CurrentCustomer;
        if (customer is null) return false;

        var dbBase = await _customerDbFactory.CreateAsync(customer.Id, ct).ConfigureAwait(false);
        if (dbBase is not CustomerDbContext db)
        {
            throw new InvalidOperationException($"Customer DB factory returned '{dbBase.GetType().Name}' (expected CustomerDbContext).");
        }

        var user = await db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email, ct)
            .ConfigureAwait(false);

        if (user is null) return false;

        var ok = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        if (!ok) return false;

        var claims = new List<Claim>
        {
            new("CustomerId", customer.Id.ToString()),
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

