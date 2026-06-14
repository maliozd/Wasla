using FluentValidation;
using OrderHub.Application.Abstractions.Onboarding.Signup;
using OrderHub.Application.Abstractions.Plans;

namespace OrderHub.Application.Onboarding;

public sealed class CustomerSignupRequestValidator : AbstractValidator<CustomerSignupRequest>
{
    public CustomerSignupRequestValidator(IOrderHubPlanCatalog planCatalog)
    {
        RuleFor(x => x.BusinessName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Slug)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[a-z0-9][a-z0-9_-]*$")
            .WithMessage("Validation.SlugInvalid");

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

        RuleFor(x => x.Phone).MaximumLength(50).When(x => !string.IsNullOrWhiteSpace(x.Phone));
        RuleFor(x => x.City).MaximumLength(100).When(x => !string.IsNullOrWhiteSpace(x.City));
        RuleFor(x => x.Country).MaximumLength(100).When(x => !string.IsNullOrWhiteSpace(x.Country));
        RuleFor(x => x.BusinessType).MaximumLength(100).When(x => !string.IsNullOrWhiteSpace(x.BusinessType));
    }
}
