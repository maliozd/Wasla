using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// The signed-in employee's own choice to confirm Approve and Mark ready. The tenant comes from server-side resolution
/// and the user from the session; nothing in the request names either. Only employees who can act on orders use it.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageOrders)]
[Route("order-action-preference")]
public sealed class OrderActionPreferenceController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IUserOrderActionPreferenceService _preferences;

    public OrderActionPreferenceController(ICurrentTenantService currentTenant, IUserOrderActionPreferenceService preferences)
    {
        _currentTenant = currentTenant;
        _preferences = preferences;
    }

    [HttpGet("current")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var confirm = await _preferences.GetConfirmRoutineActionsAsync(tenant.Id, CurrentUserId, ct);
        return Json(new { confirmRoutineActions = confirm });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update([FromForm] bool? confirmRoutineActions, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (confirmRoutineActions is null)
            return BadRequest();

        // The response carries what was stored, so the page never shows an unsaved value.
        var saved = await _preferences.SetConfirmRoutineActionsAsync(tenant.Id, CurrentUserId, confirmRoutineActions.Value, ct);
        return Json(new { confirmRoutineActions = saved });
    }
}
