using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Security;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

/// <summary>
/// Central Admin plan prices (WAS-147): the price in effect for every plan and billing period, every price, and the
/// form that schedules a new one. Every rule lives in <see cref="IBillingPlanPriceAdminService"/>.
/// </summary>
[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/billing/plan-prices")]
public sealed class BillingPlanPricesController : Controller
{
    private const string SuccessKey = "PlanPriceSuccess";
    private const string ErrorKey = "PlanPriceError";

    private readonly IBillingPlanPriceAdminService _service;
    private readonly IWaslaPlanCatalog _plans;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public BillingPlanPricesController(
        IBillingPlanPriceAdminService service,
        IWaslaPlanCatalog plans,
        IStringLocalizer<SharedResource> localizer)
    {
        _service = service;
        _plans = plans;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(await BuildAsync(new AdminPlanPriceForm(), new Dictionary<BillingPlanPriceField, string>(), ct));

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule([FromForm] AdminPlanPriceForm form, CancellationToken ct)
    {
        if (!CentralAdminSessionClaims.TryRead(User, out var session))
            return Forbid(AuthSchemes.CentralAdmin);

        var result = await _service.ScheduleAsync(
            new BillingPlanPriceScheduleCommand(
                form.PlanCode,
                form.BillingPeriod,
                form.AmountKurus,
                form.VatIncluded switch { "true" => true, "false" => false, _ => null },
                form.EffectiveDate),
            session.AdminUserId,
            ct);
        if (result.Succeeded)
        {
            TempData[SuccessKey] = _localizer["Admin.PlanPrices.Saved"].Value;
            return RedirectToAction(nameof(Index));
        }

        var errors = result.Errors
            .GroupBy(e => e.Field)
            .ToDictionary(g => g.Key, g => _localizer[AdminPlanPricePresenter.ErrorKey(g.First().Error)].Value);
        return View(nameof(Index), await BuildAsync(form, errors, ct));
    }

    [HttpPost("{id:guid}/withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        if (!CentralAdminSessionClaims.TryRead(User, out var session))
            return Forbid(AuthSchemes.CentralAdmin);

        var outcome = await _service.WithdrawAsync(id, session.AdminUserId, ct);
        if (outcome == BillingPlanPriceWithdrawOutcome.Withdrawn)
            TempData[SuccessKey] = _localizer["Admin.PlanPrices.Withdrawn"].Value;
        else
            TempData[ErrorKey] = _localizer["Admin.PlanPrices.WithdrawRefused"].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task<AdminPlanPricesViewModel> BuildAsync(
        AdminPlanPriceForm form,
        IReadOnlyDictionary<BillingPlanPriceField, string> errors,
        CancellationToken ct) => new()
    {
        Overview = await _service.GetOverviewAsync(ct),
        Plans = _plans.GetPublicPlans()
            .Where(p => !p.IsContactSales)
            .Select(p => new AdminPlanOption(p.PlanCode, _localizer[p.DisplayNameKey].Value))
            .ToList(),
        Form = form,
        Errors = errors,
        SuccessMessage = TempData[SuccessKey] as string,
        ErrorMessage = TempData[ErrorKey] as string
    };
}
