using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Models;

namespace Wasla.Web.Controllers;

[AllowAnonymous]
public sealed class ErrorController : Controller
{
    [Route("/error")]
    public IActionResult Index()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        Response.Headers["Cache-Control"] = "no-store";

        var traceId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrEmpty(traceId) || traceId == "00000000000000000000000000000000")
            traceId = HttpContext.TraceIdentifier;

        return View(new ErrorPageViewModel { TraceId = traceId });
    }
}
