using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Web.Areas.Admin.Models.TenantOperations;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin")]
public sealed class DashboardController : Controller
{
    private readonly ICentralAdminTenantOperationsService _operations;
    private readonly ICentralAdminPendingRegistrationService _pendingRegs;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        ICentralAdminTenantOperationsService operations,
        ICentralAdminPendingRegistrationService pendingRegs,
        ILogger<DashboardController> logger)
    {
        _operations = operations;
        _pendingRegs = pendingRegs;
        _logger = logger;
    }

    /// <summary>Operations overview from CentralDb only; no tenant database is opened here.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var overview = await _operations.GetOverviewAsync(ct).ConfigureAwait(false);
            var attention = await _pendingRegs.GetAttentionListAsync(ct).ConfigureAwait(false);
            return View(new AdminOverviewViewModel { Overview = overview, AttentionRegistrations = attention });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Admin overview could not read CentralDb ({ExceptionType}).", ex.GetType().Name);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(new AdminOverviewViewModel());
        }
    }
}
