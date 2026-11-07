using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Infrastructure.Security.RateLimiting;
using Wasla.Web.Models.Signup;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// The JSON API of the registration draft and owner phone verification (WAS-192). The signup page (<c>GET /signup</c>)
/// drives its steps through these endpoints; every business rule and validation runs here, never in the page script.
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

    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IWebHostEnvironment _environment;
    private readonly SignupDraftCookie _draftCookie;

    public SignupDraftController(
        IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtection)
    {
        _localizer = localizer;
        _environment = environment;
        _draftCookie = new SignupDraftCookie(dataProtection, environment);
    }

    // --- Draft ----------------------------------------------------------------------------------------

    /// <summary>200 <see cref="SignupDraftStateResponse"/>; <c>exists: false</c> without a live draft (an invalid cookie is cleared).</summary>
    [PublicSignupRequired]
    [HttpGet("draft")]
    public Task<IActionResult> GetDraft(CancellationToken ct) => NotImplementedYet();

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
    public Task<IActionResult> SavePlan([FromBody] SignupPlanSectionRequest? body, CancellationToken ct) => NotImplementedYet();

    /// <summary>As <see cref="SavePlan"/>. A taken or reserved address is the field error <c>Signup.SlugUnavailable</c> on <c>slug</c>. Keeps the phone proof.</summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupDraftSave)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPut("draft/restaurant")]
    public Task<IActionResult> SaveRestaurant([FromBody] SignupRestaurantSectionRequest? body, CancellationToken ct) => NotImplementedYet();

    /// <summary>
    /// As <see cref="SavePlan"/>. The password is hashed at once; an empty password keeps the stored hash. A changed
    /// mobile phone clears the phone proof and invalidates outstanding codes.
    /// </summary>
    [ValidateAntiForgeryToken]
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupDraftSave)]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [HttpPut("draft/owner")]
    public Task<IActionResult> SaveOwner([FromBody] SignupOwnerSectionRequest? body, CancellationToken ct) => NotImplementedYet();

    /// <summary>
    /// Creates the payment-pending registration from the stored, verified draft only (the request has no body).
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
    public Task<IActionResult> Submit(CancellationToken ct) => NotImplementedYet();

    // --- Owner phone verification -----------------------------------------------------------------------

    /// <summary>200 <see cref="SignupPhoneStatusResponse"/>; 409 <c>Signup.Draft.Missing</c>.</summary>
    [PublicSignupRequired]
    [HttpGet("draft/phone")]
    public Task<IActionResult> PhoneStatus(CancellationToken ct) => NotImplementedYet();

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
    public Task<IActionResult> SendPhoneCode(CancellationToken ct) => NotImplementedYet();

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
    public Task<IActionResult> VerifyPhoneCode([FromBody] SignupPhoneVerifyRequest? body, CancellationToken ct) => NotImplementedYet();

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
    public Task<IActionResult> ChangePhone([FromBody] SignupPhoneChangeRequest? body, CancellationToken ct) => NotImplementedYet();

    /// <summary>
    /// Development with the fake SMS sender only: the fake messages sent to this draft's numbers, with their codes. 404
    /// everywhere else, like the checkout payment simulator.
    /// </summary>
    [HttpGet("draft/phone/fake-outbox")]
    public Task<IActionResult> FakeSmsOutbox(CancellationToken ct) =>
        _environment.IsDevelopment() ? NotImplementedYet() : Task.FromResult<IActionResult>(NotFound());

    // --- Panel address ----------------------------------------------------------------------------------

    /// <summary>
    /// 200 <see cref="SignupAddressAvailabilityResponse"/> for the normalized label: <c>available</c>, <c>taken</c>
    /// (also reserved names) or <c>invalid</c>. Advisory only, never a reservation. 429 <c>Signup.TooManyAttempts</c>.
    /// </summary>
    [PublicSignupRequired]
    [AuthenticationRateLimit(AuthenticationRateLimitPolicies.SignupAddressAvailability)]
    [HttpGet("address-availability")]
    public Task<IActionResult> AddressAvailability([FromQuery] string? slug, CancellationToken ct) => NotImplementedYet();

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

    // Phase 1 of WAS-192 defines the contract only; the behavior follows in the implementation commits.
    private Task<IActionResult> NotImplementedYet() =>
        Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status501NotImplemented));
}
