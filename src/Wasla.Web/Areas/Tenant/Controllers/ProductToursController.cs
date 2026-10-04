using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Abstractions.Tours;
using Wasla.Application.Tours;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
[Route("product-tours")]
public sealed class ProductToursController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IUserProductTourService _tours;

    public ProductToursController(ICurrentTenantService currentTenant, IUserProductTourService tours)
    {
        _currentTenant = currentTenant;
        _tours = tours;
    }

    [HttpPost("complete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete([FromForm] string? key, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (!Enum.TryParse<UserRole>(CurrentUserRole, ignoreCase: true, out var role)
            || string.IsNullOrWhiteSpace(key)
            || ProductTourCatalog.StepsFor(key, role).Count == 0)
        {
            return BadRequest();
        }

        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("UserId");
        if (!Guid.TryParse(userIdValue, out var userId))
            return BadRequest();

        var saved = await _tours.CompleteAsync(tenant.Id, userId, key, ct);
        return Json(new { ok = saved });
    }
}
