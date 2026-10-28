using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Contact;
using Wasla.Infrastructure.Security.RateLimiting;
using Wasla.Web.Models.Marketing;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// The public contact page. A message is stored in CentralDb and emailed to the Wasla team
/// (<see cref="IContactMessageService"/>); Central Admin lists it. After a stored message the page redirects to itself
/// and shows the thank-you state, so a reload does not send it again. The page is part of the full marketing site and
/// answers 404, for GET and POST, until it is published (<see cref="MarketingSiteOptions"/>).
/// </summary>
[AllowAnonymous]
[FullMarketingSiteRequired]
public sealed class ContactController : Controller, IAuthenticationRateLimitResponder
{
    /// <summary>The TempData flag the redirect after a stored message carries to the page.</summary>
    public const string SentTempDataKey = "Wasla.ContactSent";

    private static readonly string[] BinderCheckedFields = [nameof(ContactFormViewModel.Topic), nameof(ContactFormViewModel.PrivacyNoticeAcknowledged)];

    private readonly IContactMessageService _messages;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ContactController> _logger;

    public ContactController(
        IContactMessageService messages,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ContactController> logger)
    {
        _messages = messages;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet(PublicSiteUrls.ContactPath)]
    public IActionResult Index()
    {
        var model = new ContactFormViewModel
        {
            Sent = TempData[SentTempDataKey] is true
        };
        return View(model);
    }

    // Limited per client network before anything is stored or sent (AuthenticationRateLimiting:ContactSubmission).
    [ValidateAntiForgeryToken]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.ContactSubmission)]
    [HttpPost(PublicSiteUrls.ContactPath)]
    public async Task<IActionResult> Index(ContactFormViewModel model, CancellationToken ct)
    {
        // A filled honeypot is an automated submission: it is answered like a stored message and nothing is kept.
        if (!string.IsNullOrWhiteSpace(model.Website))
        {
            _logger.LogInformation("Contact form submission ignored: the hidden field was filled.");
            TempData[SentTempDataKey] = true;
            return RedirectToAction(nameof(Index));
        }

        Validate(model);
        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await _messages.SubmitAsync(
                new ContactMessageRequest(
                    model.FullName,
                    model.RestaurantName,
                    model.Email,
                    model.NormalizedPhone(),
                    model.Topic,
                    model.Message,
                    CultureInfo.CurrentUICulture.Name,
                    model.PrivacyNoticeAcknowledged),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Contact message could not be stored. ExceptionType={ExceptionType}", ex.GetType().Name);
            model.SendFailed = true;
            return View(model);
        }

        TempData[SentTempDataKey] = true;
        return RedirectToAction(nameof(Index));
    }

    Task<IActionResult> IAuthenticationRateLimitResponder.RateLimitedAsync(ActionExecutingContext context)
    {
        var model = context.ActionArguments.Values.OfType<ContactFormViewModel>().FirstOrDefault() ?? new ContactFormViewModel();
        model.Website = null;
        LocalizeModelStateErrors();
        ModelState.AddModelError(string.Empty, _localizer["Contact.TooManyAttempts", PublicSiteUrls.ContactEmail].Value);
        var result = View(nameof(Index), model);
        result.StatusCode = StatusCodes.Status429TooManyRequests;
        return Task.FromResult<IActionResult>(result);
    }

    private void Validate(ContactFormViewModel model)
    {
        // A value the binder could not read (an unknown topic, a malformed checkbox) gets the form's own message, never
        // the framework's technical one.
        var topicUnreadable = ModelState.TryGetValue(nameof(ContactFormViewModel.Topic), out var topic) && topic.Errors.Count > 0;
        foreach (var field in BinderCheckedFields)
            ModelState.Remove(field);
        if (topicUnreadable)
            ModelState.AddModelError(nameof(ContactFormViewModel.Topic), "Validation.ContactTopicInvalid");

        foreach (var (field, errorKey) in model.Validate())
        {
            if (!(topicUnreadable && field == nameof(ContactFormViewModel.Topic)))
                ModelState.AddModelError(field, errorKey);
        }

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
