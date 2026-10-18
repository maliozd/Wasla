using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// Serves the current tenant's restaurant logo to every role that may open the Live Screen. The tenant comes from
/// server-side tenant resolution, so a request on one tenant's host can only ever read that tenant's logo.
/// <para>
/// Pages link the logo as <c>/restaurant-logo?v={version}</c>, where the version is a hash of the image. A request for
/// the current version may be cached by the browser for good; any other request (an old version, or none) must be
/// revalidated, so a replaced or removed logo is never shown from cache by a freshly rendered page.
/// </para>
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewLiveScreen)]
[Route("restaurant-logo")]
public sealed class RestaurantLogoController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IRestaurantLogoService _logos;

    public RestaurantLogoController(ICurrentTenantService currentTenant, IRestaurantLogoService logos)
    {
        _currentTenant = currentTenant;
        _logos = logos;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery(Name = "v")] string? version, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        // The stored bytes were checked to be a PNG, JPEG or WebP image; these headers keep the browser from treating
        // them as anything else, even when opened directly.
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";

        var logo = await _logos.GetAsync(tenant.Id, ct).ConfigureAwait(false);
        if (logo is null)
        {
            Response.Headers.CacheControl = "no-store";
            return NotFound();
        }

        Response.Headers.CacheControl = string.Equals(version, logo.Version, StringComparison.Ordinal)
            ? "private, max-age=31536000, immutable"
            : "private, no-cache";
        return File(logo.Content, logo.ContentType, lastModified: null, entityTag: new EntityTagHeaderValue($"\"{logo.Version}\""));
    }
}
