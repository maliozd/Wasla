using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Admin;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

[Area(AreaNames.Admin)]
[Route("admin")]
public sealed class AuthController : Controller
{
    private readonly ICentralAdminAuthService _centralAuth;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AuthController(
        ICentralAdminAuthService centralAuth,
        IStringLocalizer<SharedResource> localizer)
    {
        _centralAuth = centralAuth;
        _localizer = localizer;
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null)
    {
        return View(new AdminLoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<IActionResult> Login(AdminLoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _centralAuth.ValidateAsync(model.Email, model.Password, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var code = result.ErrorCode ?? "Admin.InvalidCredentials";
            ModelState.AddModelError(string.Empty, _localizer[code].Value);
            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.UserId!.Value.ToString()),
            new(ClaimTypes.Email, result.Email!),
            new(ClaimTypes.Name, result.DisplayName ?? "Central Admin"),
            new(ClaimTypes.Role, "CentralAdmin"),
        };

        var identity = new ClaimsIdentity(claims, AuthSchemes.CentralAdmin);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            AuthSchemes.CentralAdmin,
            principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        return Redirect("/admin");
    }

    [Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthSchemes.CentralAdmin).ConfigureAwait(false);
        ExpireCentralAdminAuthCookies();
        return Redirect("/admin/login");
    }

    private void ExpireCentralAdminAuthCookies()
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Path = "/",
            SameSite = SameSiteMode.Lax
        };

        Response.Cookies.Delete(CentralAdminAuthCookieNames.Active, options);
        Response.Cookies.Delete(CentralAdminAuthCookieNames.LegacyOrderHub, options);
        Response.Cookies.Delete(CentralAdminAuthCookieNames.LegacyOrderHubScheme, options);
        Response.Cookies.Delete(CentralAdminAuthCookieNames.LegacyAspNetCoreOrderHubScheme, options);
    }
}

