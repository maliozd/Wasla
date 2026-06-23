using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/pending-registrations")]
public sealed class PendingRegistrationsController : Controller
{
    private readonly ICentralAdminPendingRegistrationService _service;
    private readonly IPendingRegistrationProvisioningService _provisioning;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<PendingRegistrationsController> _logger;

    public PendingRegistrationsController(
        ICentralAdminPendingRegistrationService service,
        IPendingRegistrationProvisioningService provisioning,
        IStringLocalizer<SharedResource> localizer,
        ILogger<PendingRegistrationsController> logger)
    {
        _service = service;
        _provisioning = provisioning;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] PendingRegistrationAdminFilter filter = PendingRegistrationAdminFilter.All,
        CancellationToken ct = default)
    {
        var result = await _service.GetListAsync(filter, ct).ConfigureAwait(false);
        var vm = new AdminPendingRegistrationListViewModel
        {
            Items = result.Items,
            ActiveFilter = filter
        };
        return View(vm);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetDetailAsync(id, ct).ConfigureAwait(false);
        if (dto is null)
            return NotFound(_localizer["Admin.PendingReg.NotFound"].Value);

        return View(MapToViewModel(dto));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/provision")]
    public async Task<IActionResult> Provision(Guid id, CancellationToken ct)
    {
        // Re-check eligibility on the server side — do not rely on the disabled browser button
        var dto = await _service.GetDetailAsync(id, ct).ConfigureAwait(false);
        if (dto is null)
            return NotFound(_localizer["Admin.PendingReg.NotFound"].Value);

        if (!dto.IsEligibleForProvisioning)
        {
            _logger.LogWarning(
                "Central Admin provisioning rejected for registration {RegistrationId}: not eligible",
                id);
            TempData["ProvisionError"] = _localizer["Admin.PendingReg.NotEligible"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await _provisioning.ProvisionAsync(id, ct: ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Central Admin provisioning completed for registration {RegistrationId} with outcome {Outcome}",
            id,
            result.Outcome);

        if (result.IsSuccess)
        {
            TempData["ProvisionSuccess"] = result.Outcome == ProvisioningOutcome.AlreadyProvisioned
                ? _localizer["Admin.PendingReg.AlreadyProvisioned"].Value
                : _localizer["Admin.PendingReg.ProvisionSuccess"].Value;
        }
        else
        {
            TempData["ProvisionError"] = result.Message
                ?? _localizer["Admin.PendingReg.ProvisionError"].Value;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private static AdminPendingRegistrationDetailViewModel MapToViewModel(AdminPendingRegistrationDetailDto dto) => new()
    {
        Id = dto.Id,
        BusinessName = dto.BusinessName,
        Slug = dto.Slug,
        PrimaryDomain = dto.PrimaryDomain,
        PlanCode = dto.PlanCode,
        BillingPeriod = dto.BillingPeriod,
        Country = dto.Country,
        City = dto.City,
        District = dto.District,
        Neighborhood = dto.Neighborhood,
        StreetAddress = dto.StreetAddress,
        OwnerFullName = dto.OwnerFullName,
        OwnerEmail = dto.OwnerEmail,
        OwnerPhone = dto.OwnerPhone,
        BusinessPhone = dto.BusinessPhone,
        BusinessEmail = dto.BusinessEmail,
        Status = dto.Status,
        CreatedAtUtc = dto.CreatedAtUtc,
        ExpiresAtUtc = dto.ExpiresAtUtc,
        PaymentSucceededAtUtc = dto.PaymentSucceededAtUtc,
        PaymentFailedAtUtc = dto.PaymentFailedAtUtc,
        ProvisionedAtUtc = dto.ProvisionedAtUtc,
        TenantId = dto.TenantId,
        TenantDomain = dto.TenantDomain,
        IsEligibleForProvisioning = dto.IsEligibleForProvisioning
    };
}
