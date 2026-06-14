using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Auth;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Models.Auth;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Route("auth")]
public sealed class AuthController : Controller
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IAuthValidationService _authValidation;
    private readonly ISignupCompletionTokenService _signupCompletionTokens;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AuthController(
        ICurrentTenantService currentTenant,
        IAuthValidationService authValidation,
        ISignupCompletionTokenService signupCompletionTokens,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _authValidation = authValidation;
        _signupCompletionTokens = signupCompletionTokens;
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
            return Redirect("/customer-access-required");
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

    [Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Tenant);
        return Redirect("/auth/login");
    }
}

