using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Auth;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Models.Auth;

namespace OrderHub.Web.Controllers;

[Route("auth")]
public sealed class AuthController : Controller
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IAuthValidationService _authValidation;

    public AuthController(ICurrentCustomerService currentCustomer, IAuthValidationService authValidation)
    {
        _currentCustomer = currentCustomer;
        _authValidation = authValidation;
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
            ModelState.AddModelError(string.Empty, "Müşteri bulunamadı.");
            return View(model);
        }

        var session = await _authValidation.ValidateAsync(customer.Id, model.Email, model.Password, ct);
        if (session is null)
        {
            ModelState.AddModelError(string.Empty, "E-posta veya şifre yanlış.");
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

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow
            });

        var returnUrl = model.ReturnUrl;
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return Redirect("/dashboard");
    }

    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/auth/login");
    }
}

