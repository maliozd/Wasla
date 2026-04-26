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
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IAuthValidationService _authValidation;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AuthController(
        ICurrentCustomerService currentCustomer,
        IAuthValidationService authValidation,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentCustomer = currentCustomer;
        _authValidation = authValidation;
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

        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null)
        {
            ModelState.AddModelError(string.Empty, _localizer["Auth.TenantContextMissing"].Value);
            return View(model);
        }

        var session = await _authValidation.ValidateAsync(customer.Id, model.Email, model.Password, ct);
        if (session is null)
        {
            ModelState.AddModelError(string.Empty, _localizer["Auth.InvalidCredentials"].Value);
            return View(model);
        }

        var claims = new List<Claim>
        {
            new("CustomerId", session.CustomerId.ToString()),
            new("UserId", session.UserId.ToString()),
            new("Email", session.Email),
            new("Role", session.Role.ToString()),
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Email, session.Email),
            new(ClaimTypes.Role, session.Role.ToString()),
            new(ClaimTypes.Name, session.FullName),
        };

        var identity = new ClaimsIdentity(claims, AuthSchemes.Customer);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            AuthSchemes.Customer,
            principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow });

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        return Redirect("/dashboard");
    }

    [Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Customer);
        return Redirect("/auth/login");
    }
}

