using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OrderHub.Web.Controllers;

[AllowAnonymous]
[Route("admin")]
public sealed class AdminController : Controller
{
    [HttpGet("login")]
    public IActionResult Login()
    {
        return View();
    }
}
