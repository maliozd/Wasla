using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Routing;

namespace Wasla.Web.Controllers;

/// <summary>
/// Public marketing pages other than the homepage (WAS-155). Wasla POS has no implementation in this solution; its
/// page describes the product as coming soon and links only to existing flows.
/// </summary>
[AllowAnonymous]
public sealed class MarketingController : Controller
{
    [HttpGet(PublicSiteUrls.PosPath)]
    public IActionResult Pos() => View();
}
