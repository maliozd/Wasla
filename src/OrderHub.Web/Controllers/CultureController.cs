using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Web.Localization;

namespace OrderHub.Web.Controllers;

[Route("culture")]
public sealed class CultureController : Controller
{
    private readonly IWebHostEnvironment _environment;

    public CultureController(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    [HttpGet("set")]
    public IActionResult Set([FromQuery] string? culture, [FromQuery] string? returnUrl)
    {
        var chosen = SupportedCultures.NormalizeOrDefault(culture);

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(chosen)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = !_environment.IsDevelopment() || Request.IsHttps
            });

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return Redirect("/");
    }
}

