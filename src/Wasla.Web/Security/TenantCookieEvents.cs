using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;

namespace Wasla.Web.Security;

/// <summary>
/// Revalidates every tenant session whenever the tenant cookie is authenticated. A session is accepted only on its own
/// tenant's host, while its user exists in that tenant's database, is active, still has the security stamp the cookie
/// was issued with and still has the role the cookie carries. Cookies with a missing or malformed tenant id, user id,
/// role or stamp (including those issued before stamps existed) are rejected. A rejected session is signed out, which
/// deletes the cookie on that host and suppresses sliding renewal. When no tenant was resolved for the request, the
/// session simply does not authenticate it and the cookie is left alone. If the tenant database cannot be read, the
/// request fails with <see cref="TenantSessionUnavailableException"/>; the session is neither accepted, renewed nor
/// deleted.
/// </summary>
public sealed class TenantCookieEvents : CookieAuthenticationEvents
{
    private static readonly object RenewalDueKey = new();

    private readonly ICurrentTenantService _currentTenant;
    private readonly ITenantSessionValidator _validator;
    private readonly ILogger<TenantCookieEvents> _logger;

    public TenantCookieEvents(
        ICurrentTenantService currentTenant,
        ITenantSessionValidator validator,
        ILogger<TenantCookieEvents> logger)
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
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
        {
            // A central host, or a path that skips tenant resolution (/culture, /error, ...). There is no tenant to
            // check the session against, so it does not authenticate this request. The cookie is kept for its own
            // tenant host, and it is not renewed (renewal is deferred above).
            context.RejectPrincipal();
            return;
        }

        if (!TenantSessionClaims.TryRead(context.Principal, out var session))
        {
            await RejectAsync(context, "MissingOrMalformedClaims").ConfigureAwait(false);
            return;
        }

        // Compared before any database is opened: a cookie from another tenant never reaches this tenant's database.
        if (session.TenantId != tenant.Id)
        {
            await RejectAsync(context, "TenantMismatch").ConfigureAwait(false);
            return;
        }

        TenantSessionState state;
        try
        {
            state = await _validator
                .ValidateAsync(tenant.Id, session.UserId, session.Role, session.SecurityStamp, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed without signing out and without renewing: the global error page answers, and the same cookie
            // is checked again on the next request. Only the exception type is logged, and the original exception is
            // not passed on, so no SQL or connection detail reaches the error pipeline.
            _logger.LogError("Tenant session could not be validated against the tenant database ({ExceptionType}).", ex.GetType().Name);
            throw new TenantSessionUnavailableException();
        }

        if (state != TenantSessionState.Valid)
        {
            await RejectAsync(context, state.ToString()).ConfigureAwait(false);
            return;
        }

        if (context.HttpContext.Items.TryGetValue(RenewalDueKey, out var due) && due is true)
            context.ShouldRenew = true;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context, string reason)
    {
        _logger.LogInformation("Tenant session rejected: {Reason}.", reason);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
    }
}

/// <summary>
/// The tenant database could not be read while validating a tenant session. It carries no inner exception, so no SQL
/// detail reaches the error pipeline.
/// </summary>
public sealed class TenantSessionUnavailableException()
    : Exception("Tenant session validation could not be completed.");
