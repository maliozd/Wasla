using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Demos;
using Wasla.Web.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// The practice order of guided order training. Order training is Owner-only, so every action also requires the
/// TenantOwner policy, like the guided-setup commands.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageOrders)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.TenantOwner)]
[Route("orders/demo")]
public sealed class GuidedDemoController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IGuidedDemoService _demos;

    public GuidedDemoController(ICurrentTenantService currentTenant, IGuidedDemoService demos)
    {
        _currentTenant = currentTenant;
        _demos = demos;
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var session = await _demos.StartAsync(tenant.Id, CurrentUserId, ct);
        if (IsAjax() || string.Equals(Request.Form["ajax"], "1", StringComparison.Ordinal))
            return Ok(new { id = session.Id, status = session.Status.ToString() });

        return Redirect("/orders/live-display");
    }

    /// <summary>
    /// Approve, Start preparing or Mark ready on the user's own practice order
    /// (<see cref="GuidedDemoTransitions.UserActions"/>). Courier steps are never accepted here.
    /// </summary>
    [HttpPost("{id:guid}/{actionName}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Act(
        Guid id,
        string actionName,
        [FromServices] IGuidedSetupCoordinator guidedSetup,
        CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var result = await _demos.ApplyActionAsync(tenant.Id, CurrentUserId, id, actionName, ct);
        if (!result.Succeeded)
            return BadRequest(new { message = ToClientMessageKey(result.MessageKey) });

        if (result.Status is { } status)
            await guidedSetup.RecordPracticeProgressAsync(tenant.Id, CurrentUserId, User, status, ct);

        return Ok(new { message = ToClientMessageKey(result.MessageKey), status = result.Status?.ToString() });
    }

    [HttpPost("finish")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Finish(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        await _demos.FinishActiveAsync(tenant.Id, CurrentUserId, ct);
        return Ok(new { ok = true });
    }

    private bool IsAjax() =>
        string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The live-screen client looks up these dictionary keys. A resource key such as
    /// Orders.ApproveSuccess is not in that dictionary, so the toast falls back to the generic error text.
    /// </summary>
    private static string ToClientMessageKey(string messageKey) => messageKey switch
    {
        "Orders.InvalidStatusForAction" => "ordersInvalidStatusForAction",
        "Orders.ActionFailed" => "ordersActionFailed",
        "Orders.OrderActionFailed" => "ordersOrderActionFailed",
        "Orders.ApproveFailed" => "ordersApproveFailed",
        "Orders.RejectFailed" => "ordersRejectFailed",
        "Orders.ApproveSuccess" => "ordersApproveSuccess",
        "Orders.RejectSuccess" => "ordersRejectSuccess",
        "Orders.StartPreparingSuccess" => "ordersStartPreparingSuccess",
        "Orders.MarkReadySuccess" => "ordersMarkReadySuccess",
        "Orders.HandToCourierSuccess" => "ordersHandToCourierSuccess",
        "Orders.MarkDeliveredSuccess" => "ordersMarkDeliveredSuccess",
        _ => "ordersOrderActionFailed"
    };
}
