using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Wasla.Application.Abstractions.Admin;

namespace Wasla.Web.Security;

/// <summary>
/// Revalidates every Central Admin session whenever the Central Admin cookie is authenticated
/// (<see cref="ICentralAdminSessionValidator"/>). A session is accepted only while its account exists in CentralDb, is
/// active and still has the security stamp the cookie was issued with; a cookie without a readable admin id or stamp,
/// including one issued before stamps existed, is rejected. A rejected session is signed out of the Central Admin scheme
/// only, which deletes its cookie and suppresses sliding renewal; the tenant cookie is left alone. If CentralDb cannot
/// be read, <see cref="CentralAdminSessionUnavailableException"/> propagates to the global exception handler and the
/// session is neither accepted, renewed nor deleted. Registered per request (<c>EventsType</c>), so it holds no state
/// across requests.
/// </summary>
public sealed class CentralAdminCookieEvents : CookieAuthenticationEvents
{
    private static readonly object RenewalDueKey = new();

    private readonly ICentralAdminSessionValidator _validator;
    private readonly ILogger<CentralAdminCookieEvents> _logger;

    public CentralAdminCookieEvents(ICentralAdminSessionValidator validator, ILogger<CentralAdminCookieEvents> logger)
    {
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
            .ValidateAsync(context.Principal, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (state == CentralAdminSessionState.Valid)
        {
            if (context.HttpContext.Items.TryGetValue(RenewalDueKey, out var due) && due is true)
                context.ShouldRenew = true;
            return;
        }

        _logger.LogInformation("Central admin session rejected: {Reason}.", state);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
    }
}
