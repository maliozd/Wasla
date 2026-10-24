using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;

namespace Wasla.Infrastructure.Security;

/// <summary>
/// Revalidates every tenant session whenever the tenant cookie is authenticated, in Wasla.Web and Wasla.Api alike
/// (<see cref="ITenantSessionValidator"/>). A session is accepted only on its own tenant's host, while its user exists
/// in that tenant's database, is active, still has the security stamp the cookie was issued with and still has the role
/// the cookie carries. A rejected session is signed out, which deletes the cookie on that host and suppresses sliding
/// renewal. When no tenant was resolved for the request, the session simply does not authenticate it and the cookie is
/// left alone. If the tenant database cannot be read, <see cref="TenantSessionUnavailableException"/> propagates; the
/// session is neither accepted, renewed nor deleted. Each application derives from this only for its challenge and
/// forbid responses. Registered per request (<c>EventsType</c>), so it holds no state across requests.
/// </summary>
public abstract class TenantSessionCookieEvents : CookieAuthenticationEvents
{
    private static readonly object RenewalDueKey = new();

    private readonly ICurrentTenantService _currentTenant;
    private readonly ITenantSessionValidator _validator;
    private readonly ILogger _logger;

    protected TenantSessionCookieEvents(
        ICurrentTenantService currentTenant,
        ITenantSessionValidator validator,
        ILogger logger)
    {
        _currentTenant = currentTenant;
        _validator = validator;
        _logger = logger;
    }

    /// <summary>
    /// The handler decides on sliding renewal before validation. Defer it, so only a session that has just been
    /// validated is ever re-issued (see <see cref="ValidatePrincipal"/>).
    /// </summary>
    public override Task CheckSlidingExpiration(CookieSlidingExpirationContext context)
    {
        context.HttpContext.Items[RenewalDueKey] = context.ShouldRenew;
        context.ShouldRenew = false;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var state = await _validator
            .ValidateAsync(_currentTenant.CurrentTenant?.Id, context.Principal, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        switch (state)
        {
            case TenantSessionState.Valid:
                if (context.HttpContext.Items.TryGetValue(RenewalDueKey, out var due) && due is true)
                    context.ShouldRenew = true;
                return;

            case TenantSessionState.NoResolvedTenant:
                // A central host, or a path that skips tenant resolution. There is no tenant to check the session
                // against, so it does not authenticate this request. The cookie is kept for its own tenant host, and it
                // is not renewed (renewal is deferred above).
                context.RejectPrincipal();
                return;

            default:
                _logger.LogInformation("Tenant session rejected: {Reason}.", state);
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
                return;
        }
    }
}
