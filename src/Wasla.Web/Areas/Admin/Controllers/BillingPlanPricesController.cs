using System.Globalization;
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
/// form that schedules a new one. A price nothing references can be corrected in place; a referenced one is superseded
/// by a prefilled schedule form (GitHub #64). Every rule lives in <see cref="IBillingPlanPriceAdminService"/>.
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

    /// <summary>The schedule form prefilled from a price, to replace it with a new effective-dated price.</summary>
    [HttpGet("{id:guid}/supersede")]
    public async Task<IActionResult> Supersede(Guid id, CancellationToken ct)
    {
        var detail = await _service.GetAsync(id, ct);
        if (detail is null)
            return NotFoundRedirect();

        var price = detail.Price;
        var form = new AdminPlanPriceForm
        {
            PlanCode = price.PlanCode,
            BillingPeriod = price.BillingPeriod,
            AmountKurus = price.AmountKurus.ToString(CultureInfo.InvariantCulture),
            VatIncluded = price.VatIncluded ? "true" : "false",
            EffectiveDate = Iso(detail.SupersedeEffectiveDate)
        };
        return View(nameof(Index), await BuildAsync(form, new Dictionary<BillingPlanPriceField, string>(), ct, superseding: price));
    }

    [HttpGet("{id:guid}/correct")]
    public async Task<IActionResult> Correct(Guid id, CancellationToken ct)
    {
        var detail = await _service.GetAsync(id, ct);
        if (detail is null)
            return NotFoundRedirect();

        var price = detail.Price;
        var form = new AdminPlanPriceCorrectionForm
        {
            Revision = price.Revision.ToString(CultureInfo.InvariantCulture),
            AmountKurus = price.AmountKurus.ToString(CultureInfo.InvariantCulture),
            VatIncluded = price.VatIncluded ? "true" : "false",
            EffectiveDate = Iso(price.EffectiveDateInTurkey)
        };
        return View(nameof(Correct), BuildCorrection(detail, form, new Dictionary<BillingPlanPriceField, string>()));
    }

    [HttpPost("{id:guid}/correct")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCorrection(Guid id, [FromForm] AdminPlanPriceCorrectionForm form, CancellationToken ct)
    {
        if (!CentralAdminSessionClaims.TryRead(User, out var session))
            return Forbid(AuthSchemes.CentralAdmin);

        // A missing or malformed revision can match no row, so it is refused as a stale form.
        var revision = int.TryParse(form.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        var result = await _service.CorrectAsync(
            new BillingPlanPriceCorrectionCommand(
                id,
                revision,
                form.AmountKurus,
                form.VatIncluded switch { "true" => true, "false" => false, _ => null },
                form.EffectiveDate),
            session.AdminUserId,
            ct);

        switch (result.Outcome)
        {
            case BillingPlanPriceCorrectionOutcome.Corrected:
                TempData[SuccessKey] = _localizer["Admin.PlanPrices.Corrected"].Value;
                return RedirectToAction(nameof(Index));
            case BillingPlanPriceCorrectionOutcome.Unchanged:
                TempData[SuccessKey] = _localizer["Admin.PlanPrices.CorrectionUnchanged"].Value;
                return RedirectToAction(nameof(Index));
            case BillingPlanPriceCorrectionOutcome.Referenced:
                // The correction page now shows why the price cannot change and offers to supersede it.
                TempData[ErrorKey] = string.Format(
                    CultureInfo.CurrentCulture,
                    _localizer["Admin.PlanPrices.CorrectionRefusedReferenced"].Value,
                    result.ReferenceCount.ToString("N0", CultureInfo.CurrentCulture));
                return RedirectToAction(nameof(Correct), new { id });
            case BillingPlanPriceCorrectionOutcome.Stale:
                TempData[ErrorKey] = _localizer["Admin.PlanPrices.CorrectionStale"].Value;
                return RedirectToAction(nameof(Correct), new { id });
            case BillingPlanPriceCorrectionOutcome.Invalid:
                var detail = await _service.GetAsync(id, ct);
                if (detail is null)
                    return NotFoundRedirect();
                var errors = result.Errors
                    .GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => _localizer[AdminPlanPricePresenter.ErrorKey(g.First().Error)].Value);
                return View(nameof(Correct), BuildCorrection(detail, form, errors));
            default:
                return NotFoundRedirect();
        }
    }

    private IActionResult NotFoundRedirect()
    {
        TempData[ErrorKey] = _localizer["Admin.PlanPrices.NotFound"].Value;
        return RedirectToAction(nameof(Index));
    }

    private AdminPlanPriceCorrectionViewModel BuildCorrection(
        BillingPlanPriceDetail detail,
        AdminPlanPriceCorrectionForm form,
        IReadOnlyDictionary<BillingPlanPriceField, string> errors) => new()
    {
        Detail = detail,
        PlanName = _plans.GetPublicPlans().FirstOrDefault(p => p.PlanCode == detail.Price.PlanCode) is { } plan
            ? _localizer[plan.DisplayNameKey].Value
            : detail.Price.PlanCode,
        Form = form,
        Errors = errors,
        ErrorMessage = TempData[ErrorKey] as string
    };

    private async Task<AdminPlanPricesViewModel> BuildAsync(
        AdminPlanPriceForm form,
        IReadOnlyDictionary<BillingPlanPriceField, string> errors,
        CancellationToken ct,
        BillingPlanPriceRow? superseding = null) => new()
    {
        Overview = await _service.GetOverviewAsync(ct),
        Plans = _plans.GetPublicPlans()
            .Where(p => !p.IsContactSales)
            .Select(p => new AdminPlanOption(p.PlanCode, _localizer[p.DisplayNameKey].Value))
            .ToList(),
        Form = form,
        Errors = errors,
        SuccessMessage = TempData[SuccessKey] as string,
        ErrorMessage = TempData[ErrorKey] as string,
        Superseding = superseding
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
