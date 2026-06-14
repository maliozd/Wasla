using FluentValidation;
using OrderHub.Application.Abstractions.Onboarding;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Onboarding;

namespace OrderHub.Application.Onboarding;

public sealed class PendingRegistrationRequestValidator : AbstractValidator<PendingRegistrationRequest>
{
    public PendingRegistrationRequestValidator(IOrderHubPlanCatalog planCatalog)
    {
        RuleFor(x => x.BusinessName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.BusinessType)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.BusinessPhone)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.Slug)
            .NotEmpty()
            .MaximumLength(100)
            .Must(RegistrationNameNormalizer.IsValidSlugFormat)
            .WithMessage("Validation.SlugInvalid");

        RuleFor(x => x.Country)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.City)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.District)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.AddressLine1)
            .NotEmpty()
            .MaximumLength(300);

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

        RuleFor(x => x.Neighborhood).MaximumLength(100).When(x => !string.IsNullOrWhiteSpace(x.Neighborhood));
        RuleFor(x => x.AddressLine2).MaximumLength(300).When(x => !string.IsNullOrWhiteSpace(x.AddressLine2));
        RuleFor(x => x.PostalCode).MaximumLength(20).When(x => !string.IsNullOrWhiteSpace(x.PostalCode));
        RuleFor(x => x.OwnerPhone).MaximumLength(50).When(x => !string.IsNullOrWhiteSpace(x.OwnerPhone));
    }
}
