using FluentValidation;
using OrderHub.Application.Abstractions.PlatformConnections;

namespace OrderHub.Application.PlatformConnections;

public sealed class CreatePlatformConnectionCommandValidator : AbstractValidator<CreatePlatformConnectionCommand>
{
    public CreatePlatformConnectionCommandValidator()
    {
        RuleFor(x => x.Platform)
            .IsInEnum();

        RuleFor(x => x.StoreId)
            .NotEmpty().WithMessage("Validation.StoreIdRequired")
            .MaximumLength(100).WithMessage("Validation.StoreIdMaxLength");

        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("Validation.ApiKeyRequired")
            .MaximumLength(500).WithMessage("Validation.ApiKeyMaxLength");

        RuleFor(x => x.ApiSecret)
            .NotEmpty().WithMessage("Validation.ApiSecretRequired")
            .MaximumLength(500).WithMessage("Validation.ApiSecretMaxLength");

        RuleFor(x => x.SupplierId)
            .MaximumLength(100).WithMessage("Validation.SupplierIdMaxLength")
            .When(x => !string.IsNullOrWhiteSpace(x.SupplierId));

        RuleFor(x => x.ExecutorEmail)
            .MaximumLength(200).WithMessage("Validation.ExecutorEmailMaxLength")
            .EmailAddress().WithMessage("Validation.ExecutorEmailInvalid")
            .When(x => !string.IsNullOrWhiteSpace(x.ExecutorEmail));
    }
}

