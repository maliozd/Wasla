using FluentValidation;
using OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Abstractions.Signup;
using OrderHub.Application.Onboarding;
using OrderHub.Domain.Enums;

namespace OrderHub.Application.Onboarding;

public sealed class PendingRegistrationRequestValidator : AbstractValidator<PendingRegistrationRequest>
{
    public PendingRegistrationRequestValidator(
        IOrderHubPlanCatalog planCatalog,
        ISignupReferenceDataService referenceData)
    {
        RuleFor(x => x.BusinessName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.BusinessTypeCodes)
            .NotEmpty()
            .WithMessage("Validation.BusinessTypeRequired");

        RuleFor(x => x.BusinessTypeCodes)
            .MustAsync(async (codes, ct) =>
            {
                var resolved = await referenceData.ResolveBusinessTypesByCodesAsync(codes, ct);
                return resolved.Count == codes.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            })
            .WithMessage("Validation.BusinessTypeInvalid")
            .When(x => x.BusinessTypeCodes.Count > 0);

        RuleFor(x => x.BusinessPhoneType)
            .NotEmpty()
            .Must(type => string.Equals(type, nameof(BusinessPhoneType.Mobile), StringComparison.OrdinalIgnoreCase)
                          || string.Equals(type, nameof(BusinessPhoneType.Landline), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Validation.BusinessPhoneTypeInvalid");

        RuleFor(x => x.BusinessPhone)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.BusinessEmail)
            .EmailAddress()
            .WithMessage("Validation.EmailInvalid")
            .MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.BusinessEmail));

        RuleFor(x => x.Slug)
            .NotEmpty()
            .MaximumLength(100)
            .Must(RegistrationNameNormalizer.IsValidSlugFormat)
            .WithMessage("Validation.SlugInvalid");

        RuleFor(x => x.Country)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.CityId)
            .NotNull()
            .WithMessage("Validation.CityRequired")
            .When(IsTurkey);

        RuleFor(x => x.DistrictId)
            .NotNull()
            .WithMessage("Validation.DistrictRequired")
            .When(IsTurkey);

        RuleFor(x => x)
            .MustAsync(async (request, ct) =>
            {
                if (!IsTurkey(request) || request.CityId is null || request.DistrictId is null)
                    return true;

                var resolved = await referenceData.ResolveCityDistrictAsync(
                    request.CityId.Value,
                    request.DistrictId.Value,
                    request.Country,
                    ct);

                return resolved is not null;
            })
            .WithMessage("Validation.CityDistrictInvalid");

        RuleFor(x => x)
            .MustAsync(async (request, ct) =>
            {
                if (!IsTurkey(request) || request.DistrictId is null || request.NeighborhoodId is null)
                    return true;

                var resolved = await referenceData.ResolveNeighborhoodAsync(
                    request.DistrictId.Value,
                    request.NeighborhoodId.Value,
                    ct);

                return resolved is not null;
            })
            .WithMessage("Validation.NeighborhoodInvalid");

        RuleFor(x => x)
            .MustAsync(async (request, ct) =>
            {
                if (!IsTurkey(request) || request.NeighborhoodId is null || request.StreetId is null)
                    return true;

                var resolved = await referenceData.ResolveStreetAsync(
                    request.NeighborhoodId.Value,
                    request.StreetId.Value,
                    ct);

                return resolved is not null;
            })
            .WithMessage("Validation.StreetInvalid");

        RuleFor(x => x.City)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.District)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.StreetAddress)
            .NotEmpty()
            .WithMessage("Validation.StreetAddressRequired")
            .MaximumLength(300);

        RuleFor(x => x.LocationUrl)
            .Must(MapLocationUrlValidator.IsValidOptionalUrl)
            .WithMessage("Validation.LocationUrlInvalid")
            .When(x => !string.IsNullOrWhiteSpace(x.LocationUrl));

        RuleFor(x => x.OwnerFullName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.OwnerEmail)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128);

        RuleFor(x => x.PlanCode)
            .Must(code => planCatalog.IsSelfServicePlan(code))
            .WithMessage("Validation.PlanInvalid");

        RuleFor(x => x.BillingPeriod)
            .Must(p => string.Equals(p, "Monthly", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(p, "Yearly", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Validation.BillingPeriodInvalid");

        RuleFor(x => x.Neighborhood).MaximumLength(150).When(x => !string.IsNullOrWhiteSpace(x.Neighborhood));
        RuleFor(x => x.BuildingNumber).MaximumLength(30).When(x => !string.IsNullOrWhiteSpace(x.BuildingNumber));
        RuleFor(x => x.Floor).MaximumLength(20).When(x => !string.IsNullOrWhiteSpace(x.Floor));
        RuleFor(x => x.DoorNumber).MaximumLength(20).When(x => !string.IsNullOrWhiteSpace(x.DoorNumber));
        RuleFor(x => x.AddressNote).MaximumLength(300).When(x => !string.IsNullOrWhiteSpace(x.AddressNote));
        RuleFor(x => x.PostalCode).MaximumLength(20).When(x => !string.IsNullOrWhiteSpace(x.PostalCode));
        RuleFor(x => x.LocationUrl).MaximumLength(500).When(x => !string.IsNullOrWhiteSpace(x.LocationUrl));
        RuleFor(x => x.OwnerPhone).MaximumLength(50).When(x => !string.IsNullOrWhiteSpace(x.OwnerPhone));
    }

    private static bool IsTurkey(PendingRegistrationRequest request) =>
        string.Equals(request.Country.Trim(), "Türkiye", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.Country.Trim(), "Turkey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.Country.Trim(), "TR", StringComparison.OrdinalIgnoreCase);
}
