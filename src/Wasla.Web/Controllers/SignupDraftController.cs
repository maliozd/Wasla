using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Security.RateLimiting;
using Wasla.Infrastructure.Sms;
using Wasla.Web.Models.Signup;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// The JSON API of the registration draft and owner phone verification (WAS-192). The signup page (<c>GET /signup</c>)
/// drives its steps through these endpoints; every business rule and validation runs in the services, never in the page
/// script.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The draft is identified only by the <see cref="SignupDraftCookie"/>; no id is accepted from the body, query or route.</item>
/// <item>Every write validates the antiforgery token (header <c>RequestVerificationToken</c>), is closed with the rest of
/// public signup (<see cref="PublicSignupRequiredAttribute"/>: 404 without a body) and is rate limited per client network
/// (429 with <c>Retry-After</c> and a JSON error, see <see cref="IAuthenticationRateLimitResponder.RateLimitedAsync"/>).</item>
/// <item>Answers carry personal data of the browser's own draft, so nothing may be cached.</item>
/// <item>Errors use <see cref="SignupApiError"/> with the stable codes of <see cref="SignupErrorCodes"/>.</item>
/// </list>
/// The contract is in <c>docs/architecture/signup-registration.md</c>.
/// </remarks>
[AllowAnonymous]
[Route("signup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SignupDraftController : Controller, IAuthenticationRateLimitResponder
{
    /// <summary>JSON section bodies are small; anything larger is refused before binding.</summary>
    public const long MaxRequestBodyBytes = 16 * 1024;

    private readonly IRegistrationDraftService _drafts;
    private readonly IRegistrationPhoneVerificationService _phone;
    private readonly IPendingRegistrationService _registrations;
    private readonly IBillingPlanPriceReader _prices;
    private readonly WebAuthenticationRateLimiter _limiter;
    private readonly TenantOnboardingOptions _onboarding;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IWebHostEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly SignupDraftCookie _draftCookie;
    private readonly SignupRegistrationOwnership _ownership;

    public SignupDraftController(
        IRegistrationDraftService drafts,
        IRegistrationPhoneVerificationService phone,
        IPendingRegistrationService registrations,
        IBillingPlanPriceReader prices,
        WebAuthenticationRateLimiter limiter,
        IOptions<TenantOnboardingOptions> onboarding,
        IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtection,
        TimeProvider time)
    {
        _drafts = drafts;
        _phone = phone;
        _registrations = registrations;
        _prices = prices;
        _limiter = limiter;
        _onboarding = onboarding.Value;
        _localizer = localizer;
        _environment = environment;
        _time = time;
        _draftCookie = new SignupDraftCookie(dataProtection, environment);
        _ownership = new SignupRegistrationOwnership(dataProtection, environment);
    }

    // --- Draft ----------------------------------------------------------------------------------------

    /// <summary>200 <see cref="SignupDraftStateResponse"/>; <c>exists: false</c> without a live draft (an invalid cookie is cleared).</summary>
    [PublicSignupRequired]
    [HttpGet("draft")]
    public async Task<IActionResult> GetDraft(CancellationToken ct)
    {
        var draftId = _draftCookie.Read(Request);
        var draft = draftId is { } id ? await _drafts.GetAsync(id, ct) : null;
        if (draft is null && SignupDraftCookie.IsPresent(Request))
            _draftCookie.Clear(HttpContext);
        return Json(await StateAsync(draft, ct));
    }

    /// <summary>
    /// 200 <see cref="SignupDraftStateResponse"/> (creates the draft and issues its cookie when the browser has none);
    /// 400 <c>Signup.Draft.InvalidRequest</c> or <c>Signup.Draft.ValidationFailed</c>; 409 <c>Signup.Draft.Missing</c> or
    /// <c>Signup.Draft.Submitted</c>; 429 <c>Signup.TooManyAttempts</c>.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupDraftSave)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPut("draft/plan")]
    public Task<IActionResult> SavePlan([FromBody] SignupPlanSectionRequest? body, CancellationToken ct) =>
        body is null
            ? Task.FromResult(Error(SignupErrorCodes.DraftInvalidRequest))
            : SaveAsync(id => _drafts.SavePlanAsync(id, new RegistrationPlanInput(body.PlanCode, body.BillingPeriod), ct), ct);

    /// <summary>As <see cref="SavePlan"/>. A taken or reserved address is the field error <c>Signup.SlugUnavailable</c> on <c>slug</c>. Keeps the phone proof.</summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupDraftSave)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPut("draft/restaurant")]
    public Task<IActionResult> SaveRestaurant([FromBody] SignupRestaurantSectionRequest? body, CancellationToken ct) =>
        body is null
            ? Task.FromResult(Error(SignupErrorCodes.DraftInvalidRequest))
            : SaveAsync(id => _drafts.SaveRestaurantAsync(id, new RegistrationRestaurantInput(
                body.BusinessName,
                body.BusinessTypeCodes,
                body.BusinessPhoneType,
                body.BusinessPhone,
                body.BusinessEmail,
                body.Slug,
                body.Country,
                body.CityId,
                body.DistrictId,
                body.NeighborhoodId,
                body.StreetId,
                body.City,
                body.District,
                body.Neighborhood,
                body.StreetAddress,
                body.BuildingNumber,
                body.Floor,
                body.DoorNumber,
                body.AddressNote,
                body.PostalCode,
                body.LocationUrl), ct), ct);

    /// <summary>
    /// As <see cref="SavePlan"/>. The password is hashed at once; an empty password keeps the stored hash. A changed
    /// mobile phone clears the phone proof and invalidates outstanding codes.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupDraftSave)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPut("draft/owner")]
    public Task<IActionResult> SaveOwner([FromBody] SignupOwnerSectionRequest? body, CancellationToken ct) =>
        body is null
            ? Task.FromResult(Error(SignupErrorCodes.DraftInvalidRequest))
            : SaveAsync(id => _drafts.SaveOwnerAsync(id, new RegistrationOwnerInput
            {
                FullName = body.FullName,
                Email = body.Email,
                MobilePhone = body.MobilePhone,
                Password = body.Password,
                ConfirmPassword = body.ConfirmPassword
            }, ct), ct);

    /// <summary>
    /// Creates the payment-pending registration from the stored, verified draft only (the request body is ignored).
    /// 200 <see cref="SignupSubmitResponse"/>, with the ownership cookie granted and the draft cookie cleared; a repeated
    /// submit returns the same registration. 409 <c>Signup.Draft.Missing</c>, <c>Signup.Draft.Incomplete</c>,
    /// <c>Signup.Phone.NotVerified</c> or <c>Signup.SlugUnavailable</c>; 400 <c>Signup.Draft.ValidationFailed</c>;
    /// 429 <c>Signup.TooManyAttempts</c> (network limit or per-owner-email cap); 500 <c>Signup.ProvisioningFailed</c>.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupSubmission)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPost("draft/submit")]
    public async Task<IActionResult> Submit(CancellationToken ct)
    {
        if (DraftIdOrMissing() is not { } draftId)
            return Error(SignupErrorCodes.DraftMissing);

        var result = await _drafts.SubmitAsync(draftId, ct);
        if (result.Succeeded)
        {
            var registrationId = result.RegistrationId!.Value;
            // The draft becomes the registration: its browser gets the existing ownership proof, and a new visit to the
            // signup page starts a new draft.
            _ownership.Grant(
                HttpContext,
                registrationId,
                _time.GetUtcNow().AddDays(Math.Max(1, _onboarding.PendingRegistrationExpiryDays)));
            _draftCookie.Clear(HttpContext);
            return Json(new SignupSubmitResponse { RegistrationId = registrationId, RedirectUrl = RegistrationUrl(registrationId) });
        }

        if (result.ErrorCode == SignupErrorCodes.DraftMissing)
            _draftCookie.Clear(HttpContext);

        return Error(
            result.ErrorCode!,
            result.FieldErrors,
            missingSections: result.MissingSections.Count == 0 ? null : result.MissingSections);
    }

    // --- Owner phone verification -----------------------------------------------------------------------

    /// <summary>200 <see cref="SignupPhoneStatusResponse"/>; 409 <c>Signup.Draft.Missing</c>.</summary>
    [PublicSignupRequired]
    [HttpGet("draft/phone")]
    public Task<IActionResult> PhoneStatus(CancellationToken ct) =>
        PhoneAsync(id => _phone.GetStatusAsync(id, ct), ct);

    /// <summary>
    /// Sends or resends a code to the draft's current owner mobile phone (no body). 200 status <c>codeSent</c>.
    /// 409 <c>Signup.Draft.Missing</c>, <c>Signup.Draft.Submitted</c>, <c>Signup.Phone.OwnerIncomplete</c>,
    /// <c>Signup.Phone.AlreadyVerified</c>; 429 <c>Signup.Phone.ResendNotAvailable</c>, <c>Signup.Phone.Locked</c>,
    /// <c>Signup.Phone.SendLimitReached</c>, <c>Signup.TooManyAttempts</c>; 502 <c>Signup.Phone.DeliveryFailed</c>;
    /// 503 <c>Signup.Phone.Unavailable</c>, <c>Signup.Phone.CapacityReached</c>.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupPhoneSend)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPost("draft/phone/send")]
    public Task<IActionResult> SendPhoneCode(CancellationToken ct) =>
        PhoneAsync(id => _phone.SendCodeAsync(id, CultureInfo.CurrentUICulture, ct), ct);

    /// <summary>
    /// 200 status <c>verified</c>. 400 <c>Signup.Draft.InvalidRequest</c>, <c>Signup.Phone.CodeFormatInvalid</c> (not
    /// counted), <c>Signup.Phone.CodeIncorrect</c>, <c>Signup.Phone.CodeExpired</c>; 409 <c>Signup.Draft.Missing</c>,
    /// <c>Signup.Draft.Submitted</c>, <c>Signup.Phone.OwnerIncomplete</c>, <c>Signup.Phone.NoActiveCode</c>,
    /// <c>Signup.Phone.AlreadyVerified</c>; 429 <c>Signup.Phone.Locked</c>, <c>Signup.TooManyAttempts</c>.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupPhoneVerify)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPost("draft/phone/verify")]
    public Task<IActionResult> VerifyPhoneCode([FromBody] SignupPhoneVerifyRequest? body, CancellationToken ct) =>
        body is null
            ? Task.FromResult(Error(SignupErrorCodes.DraftInvalidRequest))
            : PhoneAsync(id => _phone.VerifyCodeAsync(id, body.Code, ct), ct);

    /// <summary>
    /// Changes the owner's mobile phone (sends nothing). 200 status (<c>notSent</c> for a new number; unchanged for the
    /// same number). 400 <c>Signup.Draft.InvalidRequest</c> or <c>Signup.Draft.ValidationFailed</c> (field
    /// <c>owner.mobilePhone</c>); 409 <c>Signup.Draft.Missing</c>, <c>Signup.Draft.Submitted</c>,
    /// <c>Signup.Phone.OwnerIncomplete</c>; 429 <c>Signup.TooManyAttempts</c>.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupDraftSave)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPut("draft/phone")]
    public Task<IActionResult> ChangePhone([FromBody] SignupPhoneChangeRequest? body, CancellationToken ct) =>
        body is null
            ? Task.FromResult(Error(SignupErrorCodes.DraftInvalidRequest))
            : PhoneAsync(id => _phone.ChangeNumberAsync(id, body.MobilePhone, ct), ct, extendsDraft: true);

    /// <summary>
    /// Development with the fake SMS sender only: the fake messages sent to this draft's current number, with their codes.
    /// 404 everywhere else, like the checkout payment simulator.
    /// </summary>
    [HttpGet("draft/phone/fake-outbox")]
    public async Task<IActionResult> FakeSmsOutbox(CancellationToken ct)
    {
        var outbox = _environment.IsDevelopment() ? HttpContext.RequestServices.GetService<FakeSmsOutbox>() : null;
        if (outbox is null)
            return NotFound();

        if (DraftIdOrMissing() is not { } draftId || await _drafts.GetAsync(draftId, ct) is not { } draft)
            return Error(SignupErrorCodes.DraftMissing);

        var phone = draft.Phone.PhoneE164;
        var messages = phone is null
            ? Array.Empty<SignupFakeSmsMessage>()
            : outbox.MessagesTo(phone).Select(m => new SignupFakeSmsMessage
            {
                MaskedPhone = TurkishMobilePhone.Mask(m.ToE164),
                Code = m.Code,
                SentAtUtc = m.SentAtUtc
            }).ToArray();
        return Json(new SignupFakeSmsOutboxResponse { Messages = messages });
    }

    // --- Panel address ----------------------------------------------------------------------------------

    /// <summary>
    /// 200 <see cref="SignupAddressAvailabilityResponse"/> for the normalized label: <c>available</c>, <c>taken</c>
    /// (also reserved names) or <c>invalid</c>. Advisory only, never a reservation. 429 <c>Signup.TooManyAttempts</c>.
    /// </summary>
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupAddressAvailability)]
    [HttpGet("address-availability")]
    public async Task<IActionResult> AddressAvailability([FromQuery] string? slug, CancellationToken ct)
    {
        var normalized = RegistrationNameNormalizer.NormalizeSlug(slug ?? string.Empty);
        var status = !RegistrationNameNormalizer.IsValidSlugFormat(normalized)
            ? SignupAddressAvailabilityStatuses.Invalid
            : await _registrations.IsSlugAvailableAsync(normalized, ct)
                ? SignupAddressAvailabilityStatuses.Available
                : SignupAddressAvailabilityStatuses.Taken;
        return Json(new SignupAddressAvailabilityResponse { Slug = normalized, Status = status });
    }

    /// <summary>
    /// 429 with <c>Retry-After</c> (already set by the filter) and <see cref="SignupApiError"/>
    /// <c>Signup.TooManyAttempts</c>; nothing is read or written.
    /// </summary>
    Task<IActionResult> IAuthenticationRateLimitResponder.RateLimitedAsync(ActionExecutingContext context)
    {
        int? retryAfterSeconds = int.TryParse(context.HttpContext.Response.Headers.RetryAfter.ToString(), out var seconds)
            ? seconds
            : null;
        return Task.FromResult<IActionResult>(new JsonResult(new SignupApiError
        {
            Code = SignupErrorCodes.TooManyAttempts,
            Message = _localizer[SignupErrorCodes.TooManyAttempts].Value,
            RetryAfterSeconds = retryAfterSeconds
        })
        {
            StatusCode = StatusCodes.Status429TooManyRequests
        });
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    /// <summary>
    /// The draft id for a write that may create a draft. A cookie that names no draft is an error (the browser expected
    /// its draft); without a cookie the write creates one, after the draft creation limit.
    /// </summary>
    private async Task<IActionResult> SaveAsync(Func<Guid?, Task<RegistrationDraftSaveResult>> save, CancellationToken ct)
    {
        Guid? draftId = null;
        if (SignupDraftCookie.IsPresent(Request))
        {
            if (DraftIdOrMissing() is not { } id)
                return Error(SignupErrorCodes.DraftMissing);
            draftId = id;
        }
        else if (!TryAcquireDraftCreation(out var retryAfter))
        {
            return Error(SignupErrorCodes.TooManyAttempts, retryAfter: retryAfter);
        }

        var result = await save(draftId);
        if (result.Succeeded)
        {
            var draft = result.Draft!;
            _draftCookie.Issue(HttpContext, draft.Id, new DateTimeOffset(DateTime.SpecifyKind(draft.ExpiresAtUtc, DateTimeKind.Utc)));
            return Json(await StateAsync(draft, ct));
        }

        if (result.ErrorCode == SignupErrorCodes.DraftMissing)
            _draftCookie.Clear(HttpContext);

        return Error(
            result.ErrorCode!,
            result.FieldErrors,
            registrationUrl: result.ErrorCode == SignupErrorCodes.DraftSubmitted && draftId is { } submitted
                ? await SubmittedRegistrationUrlAsync(submitted, ct)
                : null);
    }

    private async Task<IActionResult> PhoneAsync(Func<Guid, Task<PhoneVerificationResult>> operation, CancellationToken ct, bool extendsDraft = false)
    {
        if (DraftIdOrMissing() is not { } draftId)
            return Error(SignupErrorCodes.DraftMissing);

        var result = await operation(draftId);
        if (result.Succeeded)
        {
            if (extendsDraft && await _drafts.GetAsync(draftId, ct) is { } draft)
                _draftCookie.Issue(HttpContext, draft.Id, new DateTimeOffset(DateTime.SpecifyKind(draft.ExpiresAtUtc, DateTimeKind.Utc)));
            return Json(SignupDraftResponses.Phone(result.Status!));
        }

        if (result.ErrorCode == SignupErrorCodes.DraftMissing)
            _draftCookie.Clear(HttpContext);

        return Error(
            result.ErrorCode!,
            result.FieldErrors,
            result.RetryAfter,
            result.Status,
            registrationUrl: result.ErrorCode == SignupErrorCodes.DraftSubmitted
                ? await SubmittedRegistrationUrlAsync(draftId, ct)
                : null);
    }

    /// <summary>The draft id from a valid cookie; a present but unreadable cookie is cleared.</summary>
    private Guid? DraftIdOrMissing()
    {
        var draftId = _draftCookie.Read(Request);
        if (draftId is null && SignupDraftCookie.IsPresent(Request))
            _draftCookie.Clear(HttpContext);
        return draftId;
    }

    private bool TryAcquireDraftCreation(out TimeSpan retryAfter)
    {
        const string policy = AuthenticationRateLimitPolicies.SignupDraftCreation;
        using var lease = _limiter.AttemptAcquire(policy, HttpContext);
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = AuthenticationRateLimiting.RetryAfter(lease, _limiter.Rule(policy));
        AuthenticationRateLimiting.LogRejection(HttpContext, policy, retryAfter);
        return false;
    }

    private async Task<string?> SubmittedRegistrationUrlAsync(Guid draftId, CancellationToken ct) =>
        await _drafts.GetAsync(draftId, ct) is { SubmittedRegistrationId: { } registrationId } ? RegistrationUrl(registrationId) : null;

    private string RegistrationUrl(Guid registrationId) =>
        Url.Action(nameof(SignupController.Pending), "Signup", new { id = registrationId })!;

    private async Task<SignupDraftStateResponse> StateAsync(RegistrationDraftView? draft, CancellationToken ct)
    {
        var prices = draft?.Plan is null ? Array.Empty<BillingPriceQuote>() : await _prices.GetPricesInEffectAsync(ct);
        return SignupDraftResponses.State(draft, _phone.IsAvailable, prices, _localizer, RegistrationUrl, _time.GetUtcNow().UtcDateTime);
    }

    private IActionResult Error(
        string code,
        IReadOnlyList<RegistrationDraftFieldError>? fieldErrors = null,
        TimeSpan? retryAfter = null,
        PhoneVerificationStatus? phone = null,
        IReadOnlyList<RegistrationDraftSection>? missingSections = null,
        string? registrationUrl = null)
    {
        var status = SignupDraftResponses.StatusCodeFor(code);
        var seconds = status is StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable
            ? SignupDraftResponses.Seconds(retryAfter)
            : null;
        if (seconds is > 0)
        {
            Response.Headers.RetryAfter = seconds.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            seconds = null;
        }

        return new JsonResult(new SignupApiError
        {
            Code = code,
            Message = _localizer[code].Value,
            FieldErrors = SignupDraftResponses.FieldErrors(fieldErrors ?? Array.Empty<RegistrationDraftFieldError>(), _localizer),
            RetryAfterSeconds = seconds,
            Phone = phone is null ? null : SignupDraftResponses.Phone(phone),
            MissingSections = missingSections?.Select(SignupDraftResponses.Section).ToArray(),
            RegistrationUrl = registrationUrl
        })
        {
            StatusCode = status
        };
    }
}
