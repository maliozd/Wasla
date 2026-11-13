using FluentValidation;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Domain.Enums;

namespace Wasla.Application.Onboarding;

/// <summary>
/// Every rule of a registration: the plan, restaurant and owner rules of <see cref="PendingRegistrationRules"/>, composed.
/// The draft validates each section with the same rules (<see cref="PendingRegistrationRules.ForSection"/>), so there is
/// one rule set. Every rule's message is a SharedResource key, which the draft API returns as the field error code.
/// </summary>
public sealed class PendingRegistrationRequestValidator : AbstractValidator<PendingRegistrationRequest>
{
    public PendingRegistrationRequestValidator(
        IWaslaPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData)
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        PendingRegistrationRules.AddPlanRules(this, planCatalog);
        PendingRegistrationRules.AddRestaurantRules(this, referenceData);
        PendingRegistrationRules.AddOwnerRules(this, passwordRequired: true);
    }
}

/// <summary>The registration rules by form section (WAS-192), and where each property belongs.</summary>
public static class PendingRegistrationRules
{
    public const string TooLong = "Validation.TooLong";

    /// <summary>The rules of one section only. Other sections' properties are not checked.</summary>
    /// <param name="passwordRequired">Owner section: false when a password hash is already stored and none was typed.</param>
    public static IValidator<PendingRegistrationRequest> ForSection(
        RegistrationDraftSection section,
        IWaslaPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData,
        bool passwordRequired = true) =>
        new SectionValidator([section], planCatalog, referenceData, passwordRequired);

    /// <summary>
    /// Every section, for the values a draft stores: the password is already a hash, so the password rules do not
    /// apply. Used again at submit.
    /// </summary>
    public static IValidator<PendingRegistrationRequest> ForStoredDraft(
        IWaslaPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData) =>
        new SectionValidator(
            [RegistrationDraftSection.Plan, RegistrationDraftSection.Restaurant, RegistrationDraftSection.Owner],
            planCatalog,
            referenceData,
            passwordRequired: false);

    /// <summary>The section and camelCase JSON field of a <see cref="PendingRegistrationRequest"/> property, or null.</summary>
    public static (RegistrationDraftSection Section, string Field)? FieldOf(string propertyName) => propertyName switch
    {
        nameof(PendingRegistrationRequest.PlanCode) => (RegistrationDraftSection.Plan, "planCode"),
        nameof(PendingRegistrationRequest.BillingPeriod) => (RegistrationDraftSection.Plan, "billingPeriod"),
        nameof(PendingRegistrationRequest.BusinessName) => (RegistrationDraftSection.Restaurant, "businessName"),
        nameof(PendingRegistrationRequest.BusinessTypeCodes) => (RegistrationDraftSection.Restaurant, "businessTypeCodes"),
        nameof(PendingRegistrationRequest.BusinessPhoneType) => (RegistrationDraftSection.Restaurant, "businessPhoneType"),
        nameof(PendingRegistrationRequest.BusinessPhone) => (RegistrationDraftSection.Restaurant, "businessPhone"),
        nameof(PendingRegistrationRequest.BusinessEmail) => (RegistrationDraftSection.Restaurant, "businessEmail"),
        nameof(PendingRegistrationRequest.Slug) => (RegistrationDraftSection.Restaurant, "slug"),
        nameof(PendingRegistrationRequest.Country) => (RegistrationDraftSection.Restaurant, "country"),
        nameof(PendingRegistrationRequest.CityId) => (RegistrationDraftSection.Restaurant, "cityId"),
        nameof(PendingRegistrationRequest.DistrictId) => (RegistrationDraftSection.Restaurant, "districtId"),
        nameof(PendingRegistrationRequest.NeighborhoodId) => (RegistrationDraftSection.Restaurant, "neighborhoodId"),
        nameof(PendingRegistrationRequest.StreetId) => (RegistrationDraftSection.Restaurant, "streetId"),
        nameof(PendingRegistrationRequest.City) => (RegistrationDraftSection.Restaurant, "city"),
        nameof(PendingRegistrationRequest.District) => (RegistrationDraftSection.Restaurant, "district"),
        nameof(PendingRegistrationRequest.Neighborhood) => (RegistrationDraftSection.Restaurant, "neighborhood"),
        nameof(PendingRegistrationRequest.StreetAddress) => (RegistrationDraftSection.Restaurant, "streetAddress"),
        nameof(PendingRegistrationRequest.BuildingNumber) => (RegistrationDraftSection.Restaurant, "buildingNumber"),
        nameof(PendingRegistrationRequest.Floor) => (RegistrationDraftSection.Restaurant, "floor"),
        nameof(PendingRegistrationRequest.DoorNumber) => (RegistrationDraftSection.Restaurant, "doorNumber"),
        nameof(PendingRegistrationRequest.AddressNote) => (RegistrationDraftSection.Restaurant, "addressNote"),
        nameof(PendingRegistrationRequest.PostalCode) => (RegistrationDraftSection.Restaurant, "postalCode"),
        nameof(PendingRegistrationRequest.LocationUrl) => (RegistrationDraftSection.Restaurant, "locationUrl"),
        nameof(PendingRegistrationRequest.OwnerFullName) => (RegistrationDraftSection.Owner, "fullName"),
        nameof(PendingRegistrationRequest.OwnerEmail) => (RegistrationDraftSection.Owner, "email"),
        nameof(PendingRegistrationRequest.OwnerPhone) => (RegistrationDraftSection.Owner, "mobilePhone"),
        nameof(PendingRegistrationRequest.Password) => (RegistrationDraftSection.Owner, "password"),
        _ => null
    };

    public static bool IsTurkey(string? country) =>
        !string.IsNullOrWhiteSpace(country)
        && (string.Equals(country.Trim(), "Türkiye", StringComparison.OrdinalIgnoreCase)
            || string.Equals(country.Trim(), "Turkey", StringComparison.OrdinalIgnoreCase)
            || string.Equals(country.Trim(), "TR", StringComparison.OrdinalIgnoreCase));

    public static void AddPlanRules(AbstractValidator<PendingRegistrationRequest> v, IWaslaPlanCatalog planCatalog)
    {
        v.RuleFor(x => x.PlanCode)
            .Must(code => planCatalog.FindByCode(code) is not { IsContactSales: true })
            .WithMessage("Signup.EnterpriseContactOnly")
            .Must(code => planCatalog.IsSelfServicePlan(code))
            .WithMessage("Validation.PlanInvalid");

        v.RuleFor(x => x.BillingPeriod)
            .Must(p => string.Equals(p, "Monthly", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(p, "Yearly", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Validation.BillingPeriodInvalid");
    }

    public static void AddRestaurantRules(AbstractValidator<PendingRegistrationRequest> v, ISignupReferenceDataService referenceData)
    {
        v.RuleFor(x => x.BusinessName)
            .NotEmpty().WithMessage("Validation.BusinessNameRequired")
            .MaximumLength(200).WithMessage(TooLong);

        v.RuleFor(x => x.BusinessTypeCodes)
            .NotEmpty()
            .WithMessage("Validation.BusinessSubtypeRequired")
            .MustAsync(async (codes, ct) =>
            {
                var resolved = await referenceData.ResolveBusinessTypesByCodesAsync(codes, ct);
                return resolved.Count == codes.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            })
            .WithMessage("Validation.BusinessTypeInvalid");

        v.RuleFor(x => x.BusinessPhoneType)
            .NotEmpty().WithMessage("Validation.BusinessPhoneTypeInvalid")
            .Must(type => string.Equals(type, nameof(BusinessPhoneType.Mobile), StringComparison.OrdinalIgnoreCase)
                          || string.Equals(type, nameof(BusinessPhoneType.Landline), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Validation.BusinessPhoneTypeInvalid");

        // Too many digits for a Türkiye number is refused, never cut (the page keeps them in sight). Shorter numbers and
        // the rest of the format stay unchecked, as before.
        v.RuleFor(x => x.BusinessPhone)
            .NotEmpty().WithMessage("Validation.Required")
            .MaximumLength(50).WithMessage(TooLong)
            .Must(phone => !TurkishBusinessPhone.IsTooLong(phone)).WithMessage("Validation.BusinessPhoneTooLong");

        v.RuleFor(x => x.BusinessEmail)
            .EmailAddress().WithMessage("Validation.EmailInvalid")
            .MaximumLength(256).WithMessage(TooLong)
            .When(x => !string.IsNullOrWhiteSpace(x.BusinessEmail));

        v.RuleFor(x => x.Slug)
            .NotEmpty().WithMessage("Validation.SlugRequired")
            .MaximumLength(100).WithMessage(TooLong)
            .Must(RegistrationNameNormalizer.IsValidSlugFormat)
            .WithMessage("Validation.SlugInvalid");

        v.RuleFor(x => x.Country)
            .NotEmpty().WithMessage("Validation.Required")
            .MaximumLength(100).WithMessage(TooLong);

        v.RuleFor(x => x.CityId)
            .NotNull().WithMessage("Validation.CityRequired")
            .GreaterThan(0).WithMessage("Validation.CityRequired")
            .When(x => IsTurkey(x.Country));

        v.RuleFor(x => x.DistrictId)
            .NotNull().WithMessage("Validation.DistrictRequired")
            .GreaterThan(0).WithMessage("Validation.DistrictRequired")
            .MustAsync(async (request, districtId, ct) =>
                request.CityId is not > 0
                || await referenceData.ResolveCityDistrictAsync(request.CityId.Value, districtId!.Value, request.Country, ct) is not null)
            .WithMessage("Validation.CityDistrictInvalid")
            .When(x => IsTurkey(x.Country));

        v.RuleFor(x => x.NeighborhoodId)
            .MustAsync(async (request, neighborhoodId, ct) =>
                request.DistrictId is not > 0
                || await referenceData.ResolveNeighborhoodAsync(request.DistrictId.Value, neighborhoodId!.Value, ct) is not null)
            .WithMessage("Validation.NeighborhoodInvalid")
            .When(x => IsTurkey(x.Country) && x.NeighborhoodId is not null);

        v.RuleFor(x => x.StreetId)
            .MustAsync(async (request, streetId, ct) =>
                request.NeighborhoodId is null
                || await referenceData.ResolveStreetAsync(request.NeighborhoodId.Value, streetId!.Value, ct) is not null)
            .WithMessage("Validation.StreetInvalid")
            .When(x => IsTurkey(x.Country) && x.StreetId is not null);

        v.RuleFor(x => x.City).MaximumLength(100).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.City));
        v.RuleFor(x => x.District).MaximumLength(100).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.District));

        v.RuleFor(x => x.StreetAddress)
            .NotEmpty().WithMessage("Validation.StreetAddressRequired")
            .MaximumLength(300).WithMessage(TooLong);

        v.RuleFor(x => x.LocationUrl)
            .MaximumLength(500).WithMessage(TooLong)
            .Must(MapLocationUrlValidator.IsValidOptionalUrl)
            .WithMessage("Validation.LocationUrlInvalid")
            .When(x => !string.IsNullOrWhiteSpace(x.LocationUrl));

        // A Türkiye address names its neighbourhood or village. The name need not be in the catalog: NeighborhoodId is
        // optional, so a place the catalog lacks is still a valid address.
        v.RuleFor(x => x.Neighborhood)
            .NotEmpty().WithMessage("Validation.NeighborhoodRequired")
            .When(x => IsTurkey(x.Country));
        v.RuleFor(x => x.Neighborhood).MaximumLength(150).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.Neighborhood));
        v.RuleFor(x => x.BuildingNumber).MaximumLength(30).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.BuildingNumber));
        v.RuleFor(x => x.Floor).MaximumLength(20).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.Floor));
        v.RuleFor(x => x.DoorNumber).MaximumLength(20).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.DoorNumber));
        v.RuleFor(x => x.AddressNote).MaximumLength(300).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.AddressNote));
        v.RuleFor(x => x.PostalCode).MaximumLength(20).WithMessage(TooLong).When(x => !string.IsNullOrWhiteSpace(x.PostalCode));
    }

    /// <param name="passwordRequired">False when a stored hash already stands for the password.</param>
    public static void AddOwnerRules(AbstractValidator<PendingRegistrationRequest> v, bool passwordRequired)
    {
        v.RuleFor(x => x.OwnerFullName)
            .NotEmpty().WithMessage("Validation.FullNameRequired")
            .MaximumLength(200).WithMessage(TooLong);

        v.RuleFor(x => x.OwnerEmail)
            .NotEmpty().WithMessage("Validation.EmailRequired")
            .MaximumLength(256).WithMessage(TooLong)
            .EmailAddress().WithMessage("Validation.EmailInvalid");

        // WAS-192: the owner's mobile phone is required and verified by SMS; Türkiye mobile numbers only.
        v.RuleFor(x => x.OwnerPhone)
            .NotEmpty().WithMessage("Validation.MobilePhoneRequired")
            .Must(TurkishMobilePhone.IsValid).WithMessage("Validation.MobilePhoneInvalid");

        if (passwordRequired)
        {
            v.RuleFor(x => x.Password)
                .NotEmpty().WithMessage("Validation.PasswordRequired")
                .MinimumLength(8).WithMessage("Validation.PasswordMinLength")
                .MaximumLength(128).WithMessage(TooLong);
        }
    }

    private sealed class SectionValidator : AbstractValidator<PendingRegistrationRequest>
    {
        public SectionValidator(
            IReadOnlyCollection<RegistrationDraftSection> sections,
            IWaslaPlanCatalog planCatalog,
            ISignupReferenceDataService referenceData,
            bool passwordRequired)
        {
            RuleLevelCascadeMode = CascadeMode.Stop;
            if (sections.Contains(RegistrationDraftSection.Plan))
                AddPlanRules(this, planCatalog);
            if (sections.Contains(RegistrationDraftSection.Restaurant))
                AddRestaurantRules(this, referenceData);
            if (sections.Contains(RegistrationDraftSection.Owner))
                AddOwnerRules(this, passwordRequired);
        }
    }
}
