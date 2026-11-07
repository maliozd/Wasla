namespace Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;

/// <summary>
/// The browser-bound, server-side registration draft (WAS-192): the signup form's sections are saved here, step by step,
/// before anything is payment-pending. A draft is identified only by the id in its browser's protected draft cookie; a
/// caller never takes the id from a request body, query or route.
/// </summary>
/// <remarks>
/// <para>Each save validates its section with the rules of <c>PendingRegistrationRequestValidator</c> for that section and
/// stores it only when it is valid. A draft lives for an idle lifetime that every successful save extends, up to a maximum
/// lifetime; an expired draft is treated as missing and later deleted.</para>
/// <para><see cref="SubmitAsync"/> is the only path that creates a <c>PendingRegistration</c> for a signup: it builds the
/// registration from the stored draft alone, after the owner's current mobile phone has been verified.</para>
/// </remarks>
public interface IRegistrationDraftService
{
    /// <summary>The live draft, or null when it does not exist, has expired, or was cleaned up.</summary>
    Task<RegistrationDraftView?> GetAsync(Guid draftId, CancellationToken ct);

    /// <summary>
    /// Validates and saves the plan section. With <paramref name="draftId"/> null a new draft is created for a valid
    /// section (<see cref="RegistrationDraftSaveResult.Created"/>); an id that names no live draft is
    /// <see cref="SignupErrorCodes.DraftMissing"/>; a submitted draft is <see cref="SignupErrorCodes.DraftSubmitted"/>.
    /// </summary>
    Task<RegistrationDraftSaveResult> SavePlanAsync(Guid? draftId, RegistrationPlanInput input, CancellationToken ct);

    /// <summary>
    /// Validates and saves the restaurant section, as <see cref="SavePlanAsync"/>. A taken or reserved panel address is a
    /// field error; it is advisory and is checked again at submit. The phone proof is kept.
    /// </summary>
    Task<RegistrationDraftSaveResult> SaveRestaurantAsync(Guid? draftId, RegistrationRestaurantInput input, CancellationToken ct);

    /// <summary>
    /// Validates and saves the owner section, as <see cref="SavePlanAsync"/>. The password is hashed at once and only the
    /// hash is stored; the first save requires a password, a later save with an empty password keeps the stored hash.
    /// A changed mobile phone clears the phone proof and invalidates every outstanding code of the draft; an unchanged one
    /// keeps them.
    /// </summary>
    Task<RegistrationDraftSaveResult> SaveOwnerAsync(Guid? draftId, RegistrationOwnerInput input, CancellationToken ct);

    /// <summary>
    /// Creates the payment-pending registration from the stored draft (“Proceed to secure payment”). Nothing from the
    /// request is used. It re-validates every section, the plan, the panel address and the per-owner-email cap, and
    /// requires a current proof for the owner's stored mobile phone. In one transaction it marks the draft submitted,
    /// stores the registration with the password hash, the verified E.164 phone and the verification time, and clears the
    /// draft's personal data. A draft yields at most one registration: a repeated submit returns the same one.
    /// </summary>
    Task<RegistrationDraftSubmitResult> SubmitAsync(Guid draftId, CancellationToken ct);
}
