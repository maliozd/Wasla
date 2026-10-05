using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Security;

namespace Wasla.Web.Security;

/// <summary>
/// Revalidates every Central Admin session against CentralDb whenever the cookie is authenticated. A session is accepted
/// only while its account exists, is active and still has the security stamp the cookie was issued with. Cookies without
/// a readable account id or stamp (including those issued before stamps existed) are rejected. A rejected session is
/// signed out, which deletes the cookie and suppresses sliding renewal for that response. If CentralDb cannot be read,
/// the request fails with <see cref="CentralAdminSessionUnavailableException"/> and the session is neither accepted,
/// renewed nor deleted.
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
        if (!TryReadSession(context.Principal, out var adminUserId, out var securityStamp))
        {
            await RejectAsync(context, "MissingOrMalformedClaims").ConfigureAwait(false);
            return;
        }

        CentralAdminSessionState state;
        try
        {
            state = await _validator
                .ValidateAsync(adminUserId, securityStamp, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed without signing out and without renewing (renewal is deferred above): the global error page
            // answers 503, and the same cookie is checked again on the next request. Only the exception type is logged,
            // as elsewhere in Admin, and the original exception is not passed on.
            _logger.LogError("Central admin session could not be validated against CentralDb ({ExceptionType}).", ex.GetType().Name);
            throw new CentralAdminSessionUnavailableException();
        }

        if (state != CentralAdminSessionState.Valid)
        {
            await RejectAsync(context, state.ToString()).ConfigureAwait(false);
            return;
        }

        if (context.HttpContext.Items.TryGetValue(RenewalDueKey, out var due) && due is true)
            context.ShouldRenew = true;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context, string reason)
    {
        _logger.LogInformation("Central admin session rejected: {Reason}.", reason);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
    }

    private static bool TryReadSession(ClaimsPrincipal? principal, out Guid adminUserId, out Guid securityStamp)
    {
        securityStamp = Guid.Empty;
        return Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out adminUserId)
               && adminUserId != Guid.Empty
               && Guid.TryParse(principal?.FindFirstValue(WaslaAuthContracts.CentralAdminSecurityStampClaim), out securityStamp)
               && securityStamp != Guid.Empty;
    }
}

/// <summary>
/// CentralDb could not be read while validating a Central Admin session. The global error page answers 503 for it.
/// It carries no inner exception, so no SQL detail reaches the error pipeline.
/// </summary>
public sealed class CentralAdminSessionUnavailableException()
    : Exception("Central admin session validation could not be completed.");
