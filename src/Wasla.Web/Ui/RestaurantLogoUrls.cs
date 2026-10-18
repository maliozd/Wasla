using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Routing;

namespace Wasla.Web.Ui;

public static class RestaurantLogoUrls
{
    /// <summary>
    /// The versioned URL of the current tenant's logo (served by <c>RestaurantLogoController</c>), or null without a logo.
    /// The version changes with the image, so a page rendered after a replacement never reuses the cached old image.
    /// </summary>
    public static string? RestaurantLogo(this IUrlHelper url, string? version) =>
        string.IsNullOrEmpty(version)
            ? null
            : url.Action("Index", "RestaurantLogo", new { area = AreaNames.Tenant, v = version });
}
