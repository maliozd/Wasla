using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Web.Models.Auth;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Route("auth")]
public sealed class AuthController : Controller
{
    private static readonly Regex Base64UrlTokenRegex = new(@"^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled);

    private readonly ICurrentTenantService _currentTenant;
    private readonly IAuthValidationService _authValidation;
    private readonly ITenantLoginRecorder _loginRecorder;
    private readonly ISignupCompletionTokenService _signupCompletionTokens;
    private readonly ITenantPasswordResetService _passwordReset;
    private readonly IWebHostEnvironment _environment;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AuthController(
        ICurrentTenantService currentTenant,
        IAuthValidationService authValidation,
        ITenantLoginRecorder loginRecorder,
        ISignupCompletionTokenService signupCompletionTokens,
        ITenantPasswordResetService passwordReset,
        IWebHostEnvironment environment,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _authValidation = authValidation;
        _loginRecorder = loginRecorder;
        _signupCompletionTokens = signupCompletionTokens;
        _passwordReset = passwordReset;
        _environment = environment;
        _localizer = localizer;
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public async Task<IActionResult> Login([FromQuery] string? returnUrl = null)
    {
        var tenant = _currentTenant.CurrentTenant;
        var auth = await HttpContext.AuthenticateAsync(AuthSchemes.Tenant);
        if (auth.Succeeded
            && auth.Principal?.Identity?.IsAuthenticated == true
            && tenant is not null
            && Guid.TryParse(auth.Principal.FindFirstValue("TenantId"), out var claimTenantId)
            && claimTenantId == tenant.Id)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return Redirect("/dashboard");
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
    [HttpGet("access-denied")]
    public IActionResult AccessDenied()
    {
        return View();
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
        {
            ModelState.AddModelError(string.Empty, _localizer["Auth.TenantContextMissing"].Value);
            return View(model);
        }

        var session = await _authValidation.ValidateAsync(tenant.Id, model.Email, model.Password, ct);
        if (session is null)
        {
            ModelState.AddModelError(string.Empty, _localizer["Auth.InvalidCredentials"].Value);
            return View(model);
        }

        ExpireTenantAuthCookies();
        await SignInSessionAsync(session, model.RememberMe, ct);

        // A login is a password sign-in that established the session. Credential checks alone
        // (/api/auth/validate), the signup welcome link and cookie authentication do not record one.
        await _loginRecorder.RecordSuccessfulLoginAsync(tenant.Id, session.UserId, ct);

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        return Redirect("/dashboard");
    }

    [AllowAnonymous]
    [HttpGet("welcome")]
    public async Task<IActionResult> Welcome([FromQuery] string? token, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
        {
            return Redirect("/tenant-address-required");
        }

        var payload = _signupCompletionTokens.ValidateAndConsume(token);
        // The session is built from the user's current row (role, active state, security stamp), not from the
        // values the link was issued with.
        var session = payload is null || payload.CustomerId != tenant.Id
            ? null
            : await _authValidation.GetActiveSessionAsync(tenant.Id, payload.UserId, ct);
        if (session is null)
        {
            TempData["AuthMessage"] = _localizer["Auth.WelcomeInvalid"].Value;
            return Redirect("/auth/login");
        }

        await SignInSessionAsync(session, rememberMe: false, ct);
        return Redirect("/dashboard");
    }

    [AllowAnonymous]
    [HttpGet("forgot-password")]
    public IActionResult ForgotPassword()
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return Redirect("/tenant-address-required");

        return View(new ForgotPasswordViewModel());
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.ForgotPassword)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return Redirect("/tenant-address-required");

        if (!ModelState.IsValid)
            return View(model);

        var result = await _passwordReset.RequestResetAsync(
            tenant.Id,
            model.Email,
            rawToken => TenantWelcomeUrlBuilder.BuildPasswordResetUrl(Request, _environment, tenant.PrimaryDomain, rawToken),
            System.Globalization.CultureInfo.CurrentUICulture.Name,
            ct).ConfigureAwait(false);

        return result.Status switch
        {
            TenantPasswordResetRequestStatus.EmailSent => View(new ForgotPasswordViewModel
            {
                EmailSent = true
            }),
            TenantPasswordResetRequestStatus.UserInactive => ForgotPasswordError(
                model,
                "Auth.ForgotPassword.UserInactive"),
            TenantPasswordResetRequestStatus.EmailDeliveryFailed => ForgotPasswordError(
                model,
                "Auth.ForgotPassword.EmailDeliveryFailed"),
            _ => ForgotPasswordError(
                model,
                "Auth.ForgotPassword.UserNotFound")
        };
    }

    private ViewResult ForgotPasswordError(ForgotPasswordViewModel model, string resourceKey)
    {
        ModelState.AddModelError(string.Empty, _localizer[resourceKey].Value);
        return View(model);
    }

    [AllowAnonymous]
    [HttpGet("reset-password")]
    public IActionResult ResetPassword([FromQuery] string? token)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return Redirect("/tenant-address-required");

        if (IsClearlyMalformedResetToken(token))
        {
            return View(new ResetPasswordViewModel
            {
                IsInvalidToken = true
            });
        }

        return View(new ResetPasswordViewModel { Token = token!.Trim() });
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return Redirect("/tenant-address-required");

        if (!ModelState.IsValid)
            return View(model);

        var result = await _passwordReset
            .ResetPasswordAsync(tenant.Id, model.Token, model.NewPassword, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            TempData["AuthSuccess"] = _localizer["Auth.ResetPassword.Updated"].Value;
            return RedirectToAction(nameof(Login));
        }

        if (result.Outcome == TenantPasswordResetOutcome.InvalidPassword)
        {
            foreach (var error in result.ValidationErrors)
                ModelState.AddModelError(nameof(model.NewPassword), _localizer[error].Value);

            return View(model);
        }

        model.IsInvalidToken = true;
        ModelState.AddModelError(string.Empty, _localizer["Auth.ResetPassword.InvalidOrExpired"].Value);
        return View(model);
    }

    private async Task SignInSessionAsync(AuthSessionResult session, bool rememberMe, CancellationToken ct)
    {
        _ = ct;
        // Tenant, user, role and security stamp are revalidated on every request (TenantCookieEvents).
        var claims = new List<Claim>(TenantSessionClaims.Create(
            session.CustomerId,
            session.UserId,
            session.Role,
            session.SecurityStamp))
        {
            new("Email", session.Email),
            new(ClaimTypes.Email, session.Email),
            new(ClaimTypes.Name, session.FullName),
        };

        var identity = new ClaimsIdentity(claims, AuthSchemes.Tenant);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            AuthSchemes.Tenant,
            principal,
            AuthCookiePersistence.Create(rememberMe, AuthCookiePersistence.TenantPersistentDuration));
    }

    private static bool IsClearlyMalformedResetToken(string? token)
    {
        return string.IsNullOrWhiteSpace(token) || !Base64UrlTokenRegex.IsMatch(token.Trim());
    }

    [Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Tenant);
        ExpireTenantAuthCookies();
        return Redirect("/auth/login");
    }

    private void ExpireTenantAuthCookies()
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Path = "/",
            SameSite = SameSiteMode.Lax
        };

        Response.Cookies.Delete(TenantAuthCookieNames.Active, options);
        Response.Cookies.Delete(TenantAuthCookieNames.LegacyOrderHub, options);
    }
}

