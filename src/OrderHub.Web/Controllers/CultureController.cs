using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Web.Localization;

namespace OrderHub.Web.Controllers;

[Route("culture")]
public sealed class CultureController : Controller
{
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
                Secure = true
            });

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return Redirect("/");
    }
}

