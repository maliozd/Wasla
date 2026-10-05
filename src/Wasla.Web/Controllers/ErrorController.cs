using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Models;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

[AllowAnonymous]
public sealed class ErrorController : Controller
{
    [Route("/error")]
    public IActionResult Index()
    {
        // A Central Admin session that could not be validated because CentralDb is unavailable is a 503, as on the
        // Admin pages' own CentralDb failures. Everything else stays a 500.
        Response.StatusCode = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error is CentralAdminSessionUnavailableException
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;
        Response.Headers["Cache-Control"] = "no-store";

        var traceId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrEmpty(traceId) || traceId == "00000000000000000000000000000000")
            traceId = HttpContext.TraceIdentifier;

        return View(new ErrorPageViewModel { TraceId = traceId });
    }
}
