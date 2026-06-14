using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace OrderHub.Web.Controllers;

public abstract class BaseController : Controller
{
    protected Guid CurrentTenantId =>
        Guid.Parse(User.FindFirstValue("TenantId")!);

    protected Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("UserId")!);

    protected string? CurrentUserRole =>
        User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("Role");

    protected bool IsOwnerOrManager() =>
        CurrentUserRole is "Owner" or "Manager";
}

