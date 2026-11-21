using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Abstractions.ReferenceData;
using Wasla.Infrastructure.Security.RateLimiting;
using Wasla.Web.Models.Marketing;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// The pilot restaurant application (WAS-217): <c>/pilot</c> and its privacy notice. An application is stored in
/// CentralDb as a <see cref="Domain.Enums.ContactTopic.PilotApplication"/> message and emailed to the Wasla team
/// (<see cref="IContactMessageService"/>); Central Admin lists it with the contact messages. After a stored application
/// the page redirects to itself and shows the received state, so a reload does not send it again. It starts no signup,
/// payment or provisioning. The pages answer 404 unless <see cref="PilotApplicationGate.IsOpenAsync"/>, and exist on the
/// marketing host only.
/// </summary>
[AllowAnonymous]
[PilotApplicationRequired]
public sealed class PilotController : Controller, IAuthenticationRateLimitResponder
{
    /// <summary>The TempData flag the redirect after a stored application carries to the page.</summary>
    public const string SentTempDataKey = "Wasla.PilotApplicationSent";

    private readonly IContactMessageService _messages;
    private readonly ITurkishProvinceCatalog _provinces;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly WebAuthenticationRateLimiter _limiter;
    private readonly ILogger<PilotController> _logger;

    public PilotController(
        IContactMessageService messages,
        ITurkishProvinceCatalog provinces,
        IStringLocalizer<SharedResource> localizer,
        WebAuthenticationRateLimiter limiter,
        ILogger<PilotController> logger)
    {
        _messages = messages;
        _provinces = provinces;
        _localizer = localizer;
        _limiter = limiter;
        _logger = logger;
    }

    [HttpGet(PublicSiteUrls.PilotPath)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = new PilotApplicationViewModel
        {
            SubmissionId = Guid.NewGuid(),
            Sent = TempData[SentTempDataKey] is true
        };
        if (!model.Sent)
            model.Provinces = await _provinces.GetActiveProvincesAsync(ct);
        return View(model);
    }

    // Two limits per client network: every post (AuthenticationRateLimiting:PilotApplication, generous, so corrections of
    // validation errors never lock a visitor out), and valid applications about to be stored and emailed
    // (AuthenticationRateLimiting:PilotApplicationStore), taken only after validation and the honeypot.
    [ValidateAntiForgeryToken]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.PilotApplication)]
    [HttpPost(PublicSiteUrls.PilotPath)]
    public async Task<IActionResult> Index(PilotApplicationViewModel model, CancellationToken ct)
    {
        // A filled honeypot is an automated submission: it is answered like a stored application and nothing is kept.
        if (!string.IsNullOrWhiteSpace(model.Website))
        {
            _logger.LogInformation("Pilot application ignored: the hidden field was filled.");
            TempData[SentTempDataKey] = true;
            return RedirectToAction(nameof(Index));
        }

        if (model.SubmissionId == Guid.Empty)
            model.SubmissionId = Guid.NewGuid();

        model.Provinces = await _provinces.GetActiveProvincesAsync(ct);
        Validate(model);
        if (!ModelState.IsValid)
            return View(model);

        using var storeLease = _limiter.AttemptAcquire(AuthenticationRateLimitPolicies.PilotApplicationStore, HttpContext);
        if (!storeLease.IsAcquired)
        {
            var retryAfter = AuthenticationRateLimiting.RetryAfter(storeLease, _limiter.Rule(AuthenticationRateLimitPolicies.PilotApplicationStore));
            AuthenticationRateLimiting.ApplyRejection(Response, retryAfter);
            AuthenticationRateLimiting.LogRejection(HttpContext, AuthenticationRateLimitPolicies.PilotApplicationStore, retryAfter);
            ViewData[WebAuthenticationRateLimiting.RateLimitedViewDataKey] = true;
            return TooManyAttemptsView(model);
        }

        try
        {
            await _messages.SubmitPilotApplicationAsync(
                new PilotApplicationRequest(
                    model.SubmissionId,
                    model.RestaurantName,
                    model.ContactName,
                    model.Email,
                    model.NormalizedPhone(),
                    model.CityPlateCode.Trim(),
                    model.Note,
                    CultureInfo.CurrentUICulture.Name),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service logged the cause (exception types and SQL error number, nothing the visitor typed).
            _logger.LogWarning("Pilot application could not be stored; the form is shown again. ExceptionType={ExceptionType}", ex.GetType().Name);
            model.SaveFailed = true;
            return View(model);
        }

        TempData[SentTempDataKey] = true;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet(PublicSiteUrls.PilotPrivacyNoticePath)]
    public IActionResult PrivacyNotice() => View();

    async Task<IActionResult> IAuthenticationRateLimitResponder.RateLimitedAsync(ActionExecutingContext context)
    {
        var model = context.ActionArguments.Values.OfType<PilotApplicationViewModel>().FirstOrDefault() ?? new PilotApplicationViewModel();
        model.Website = null;
        if (model.SubmissionId == Guid.Empty)
            model.SubmissionId = Guid.NewGuid();
        model.Provinces = await _provinces.GetActiveProvincesAsync(context.HttpContext.RequestAborted);
        LocalizeModelStateErrors();
        return TooManyAttemptsView(model);
    }

    /// <summary>The form again with what was typed, the limit message and status 429; nothing is stored or sent.</summary>
    private ViewResult TooManyAttemptsView(PilotApplicationViewModel model)
    {
        ModelState.AddModelError(string.Empty, _localizer["Pilot.TooManyAttempts", PublicSiteUrls.ContactEmail].Value);
        var result = View(nameof(Index), model);
        result.StatusCode = StatusCodes.Status429TooManyRequests;
        return result;
    }

    private void Validate(PilotApplicationViewModel model)
    {
        foreach (var (field, errorKey) in model.Validate())
            ModelState.AddModelError(field, errorKey);

        // A value the binder could not read (a malformed submission id) never reaches the visitor as a technical message.
        if (ModelState.TryGetValue(nameof(PilotApplicationViewModel.SubmissionId), out var id) && id.Errors.Count > 0)
            ModelState.Remove(nameof(PilotApplicationViewModel.SubmissionId));

        LocalizeModelStateErrors();
    }

    private void LocalizeModelStateErrors()
    {
        foreach (var state in ModelState.Values)
        {
            for (var i = 0; i < state.Errors.Count; i++)
            {
                var raw = state.Errors[i].ErrorMessage;
                if (string.IsNullOrEmpty(raw))
                    continue;

                var localized = _localizer[raw];
                if (!localized.ResourceNotFound)
                    state.Errors[i] = new ModelError(localized.Value);
            }
        }
    }
}
