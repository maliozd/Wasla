using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
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
    private readonly ISignupCompletionTokenService _signupCompletionTokens;
    private readonly ITenantPasswordResetService _passwordReset;
    private readonly IWebHostEnvironment _environment;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AuthController(
        ICurrentTenantService currentTenant,
        IAuthValidationService authValidation,
        ISignupCompletionTokenService signupCompletionTokens,
        ITenantPasswordResetService passwordReset,
        IWebHostEnvironment environment,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _authValidation = authValidation;
        _signupCompletionTokens = signupCompletionTokens;
        _passwordReset = passwordReset;
        _environment = environment;
        _localizer = localizer;
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null)
    {
        return View(new LoginViewModel { ReturnUrl = returnUrl });
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

        await SignInSessionAsync(session, ct);

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
        if (payload is null || payload.CustomerId != tenant.Id)
        {
            TempData["AuthMessage"] = _localizer["Auth.WelcomeInvalid"].Value;
            return Redirect("/auth/login");
        }

        await SignInSessionAsync(payload, ct);
        return Redirect("/onboarding");
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

    private async Task SignInSessionAsync(AuthSessionResult session, CancellationToken ct)
    {
        await SignInSessionAsync(new SignupCompletionPayload(
            session.CustomerId,
            session.UserId,
            session.Email,
            session.FullName,
            session.Role), ct);
    }

    private async Task SignInSessionAsync(SignupCompletionPayload session, CancellationToken ct)
    {
        _ = ct;
        var claims = new List<Claim>
        {
            new("TenantId", session.CustomerId.ToString()),
            new("UserId", session.UserId.ToString()),
            new("Email", session.Email),
            new("Role", session.Role.ToString()),
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Email, session.Email),
            new(ClaimTypes.Role, session.Role.ToString()),
            new(ClaimTypes.Name, session.FullName),
        };

        var identity = new ClaimsIdentity(claims, AuthSchemes.Tenant);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            AuthSchemes.Tenant,
            principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow });
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
        return Redirect("/auth/login");
    }
}

