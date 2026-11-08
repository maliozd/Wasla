using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Onboarding;
using Wasla.Web.Controllers;
using Wasla.Web.Formatting;

namespace Wasla.Web.Models.Signup;

/// <summary>Maps the draft and verification results to the draft API's JSON (WAS-192).</summary>
public static class SignupDraftResponses
{
    public static SignupDraftStateResponse State(
        RegistrationDraftView? draft,
        bool phoneVerificationAvailable,
        IReadOnlyList<BillingPriceQuote> prices,
        IStringLocalizer localizer,
        Func<Guid, string> registrationUrl,
        DateTime nowUtc)
    {
        if (draft is null)
        {
            return new SignupDraftStateResponse
            {
                Exists = false,
                Phone = new SignupPhoneStatusResponse
                {
                    State = phoneVerificationAvailable ? SignupPhoneStates.NotSent : SignupPhoneStates.Unavailable,
                    CodeLength = PhoneVerificationPolicy.CodeLength
                }
            };
        }

        var restaurant = draft.Restaurant;
        var owner = draft.Owner;
        return new SignupDraftStateResponse
        {
            Exists = true,
            ExpiresInSeconds = Seconds(draft.ExpiresAtUtc - nowUtc),
            Submitted = draft.IsSubmitted,
            RegistrationUrl = draft.SubmittedRegistrationId is { } id ? registrationUrl(id) : null,
            Plan = new SignupDraftPlanState
            {
                Complete = draft.Plan is not null,
                PlanCode = draft.Plan?.PlanCode,
                BillingPeriod = draft.Plan?.BillingPeriod
            },
            Restaurant = restaurant is null
                ? new SignupDraftRestaurantState()
                : new SignupDraftRestaurantState
                {
                    Complete = true,
                    BusinessName = restaurant.BusinessName,
                    BusinessTypeCodes = restaurant.BusinessTypeCodes,
                    BusinessPhoneType = restaurant.BusinessPhoneType,
                    BusinessPhone = restaurant.BusinessPhone,
                    BusinessEmail = restaurant.BusinessEmail,
                    Slug = restaurant.Slug,
                    Country = restaurant.Country,
                    CityId = restaurant.CityId,
                    DistrictId = restaurant.DistrictId,
                    NeighborhoodId = restaurant.NeighborhoodId,
                    StreetId = restaurant.StreetId,
                    City = restaurant.City,
                    District = restaurant.District,
                    Neighborhood = restaurant.Neighborhood,
                    StreetAddress = restaurant.StreetAddress,
                    BuildingNumber = restaurant.BuildingNumber,
                    Floor = restaurant.Floor,
                    DoorNumber = restaurant.DoorNumber,
                    AddressNote = restaurant.AddressNote,
                    PostalCode = restaurant.PostalCode,
                    LocationUrl = restaurant.LocationUrl
                },
            Owner = owner is null
                ? new SignupDraftOwnerState()
                : new SignupDraftOwnerState
                {
                    Complete = true,
                    FullName = owner.FullName,
                    Email = owner.Email,
                    MobilePhone = TurkishMobilePhone.NationalNumber(owner.MobilePhoneE164),
                    PasswordSet = owner.PasswordSet
                },
            Phone = Phone(draft.Phone),
            CanReview = draft.CanReview,
            Price = draft.Plan is null ? null : Price(draft.Plan, prices, localizer)
        };
    }

    public static SignupPhoneStatusResponse Phone(PhoneVerificationStatus status) => new()
    {
        State = status.State switch
        {
            PhoneVerificationState.CodeSent => SignupPhoneStates.CodeSent,
            PhoneVerificationState.Expired => SignupPhoneStates.Expired,
            PhoneVerificationState.Verified => SignupPhoneStates.Verified,
            PhoneVerificationState.Locked => SignupPhoneStates.Locked,
            PhoneVerificationState.Unavailable => SignupPhoneStates.Unavailable,
            _ => SignupPhoneStates.NotSent
        },
        MaskedPhone = status.PhoneE164 is null ? null : TurkishMobilePhone.Mask(status.PhoneE164),
        CodeLength = status.CodeLength,
        CodeExpiresInSeconds = Seconds(status.CodeExpiresIn),
        ResendAvailableInSeconds = Seconds(status.ResendAvailableIn),
        LockedForSeconds = Seconds(status.LockedFor),
        AttemptsRemaining = status.AttemptsRemaining
    };

    public static SignupDraftPriceResponse Price(RegistrationDraftPlan plan, IReadOnlyList<BillingPriceQuote> prices, IStringLocalizer localizer)
    {
        var quote = PlanPriceText.Find(prices, plan.PlanCode, plan.BillingPeriod);
        return new SignupDraftPriceResponse
        {
            PlanCode = plan.PlanCode,
            BillingPeriod = plan.BillingPeriod,
            IsSet = quote is not null,
            Amount = quote?.Price.Amount,
            Currency = quote?.Price.Currency,
            VatIncluded = quote?.VatIncluded,
            PriceText = PlanPriceText.PerPeriod(quote, plan.BillingPeriod, localizer),
            VatText = PlanPriceText.Vat(quote, localizer)
        };
    }

    public static string Section(RegistrationDraftSection section) => section switch
    {
        RegistrationDraftSection.Plan => SignupDraftSections.Plan,
        RegistrationDraftSection.Restaurant => SignupDraftSections.Restaurant,
        _ => SignupDraftSections.Owner
    };

    public static IReadOnlyList<SignupFieldError> FieldErrors(IEnumerable<RegistrationDraftFieldError> errors, IStringLocalizer localizer) =>
        errors.Select(e => new SignupFieldError
        {
            Section = Section(e.Section),
            Field = e.Field,
            Code = e.Code,
            Message = localizer[e.Code].Value
        }).ToArray();

    /// <summary>The HTTP status of an error code (docs/architecture/signup-registration.md).</summary>
    public static int StatusCodeFor(string code) => code switch
    {
        SignupErrorCodes.DraftInvalidRequest or SignupErrorCodes.DraftValidationFailed or SignupErrorCodes.PhoneCodeIncorrect
            or SignupErrorCodes.PhoneCodeExpired or SignupErrorCodes.PhoneCodeFormatInvalid => StatusCodes.Status400BadRequest,
        SignupErrorCodes.DraftMissing or SignupErrorCodes.DraftSubmitted or SignupErrorCodes.DraftIncomplete
            or SignupErrorCodes.PhoneOwnerIncomplete or SignupErrorCodes.PhoneAlreadyVerified or SignupErrorCodes.PhoneNoActiveCode
            or SignupErrorCodes.PhoneNotVerified or SignupErrorCodes.SlugUnavailable => StatusCodes.Status409Conflict,
        SignupErrorCodes.TooManyAttempts or SignupErrorCodes.PhoneResendNotAvailable or SignupErrorCodes.PhoneLocked
            or SignupErrorCodes.PhoneSendLimitReached => StatusCodes.Status429TooManyRequests,
        SignupErrorCodes.PhoneDeliveryFailed => StatusCodes.Status502BadGateway,
        SignupErrorCodes.PhoneUnavailable or SignupErrorCodes.PhoneCapacityReached => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>Whole seconds, rounded up; never negative.</summary>
    public static int? Seconds(TimeSpan? value) =>
        value is null ? null : (int)Math.Max(0, Math.Ceiling(value.Value.TotalSeconds));

    public static SignupDraftEndpoints Endpoints(IUrlHelper url, bool fakeSmsOutbox) => new(
        Draft: url.Action(nameof(SignupDraftController.GetDraft), "SignupDraft")!,
        PlanSection: url.Action(nameof(SignupDraftController.SavePlan), "SignupDraft")!,
        RestaurantSection: url.Action(nameof(SignupDraftController.SaveRestaurant), "SignupDraft")!,
        OwnerSection: url.Action(nameof(SignupDraftController.SaveOwner), "SignupDraft")!,
        Phone: url.Action(nameof(SignupDraftController.PhoneStatus), "SignupDraft")!,
        PhoneSend: url.Action(nameof(SignupDraftController.SendPhoneCode), "SignupDraft")!,
        PhoneVerify: url.Action(nameof(SignupDraftController.VerifyPhoneCode), "SignupDraft")!,
        Submit: url.Action(nameof(SignupDraftController.Submit), "SignupDraft")!,
        AddressAvailability: url.Action(nameof(SignupDraftController.AddressAvailability), "SignupDraft")!,
        Districts: url.Action(nameof(SignupController.Districts), "Signup")!,
        Neighborhoods: url.Action(nameof(SignupController.Neighborhoods), "Signup")!,
        Streets: url.Action(nameof(SignupController.Streets), "Signup")!,
        FakeSmsOutbox: fakeSmsOutbox ? url.Action(nameof(SignupDraftController.FakeSmsOutbox), "SignupDraft") : null);
}
