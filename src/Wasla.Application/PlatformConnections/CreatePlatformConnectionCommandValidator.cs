using FluentValidation;
using Wasla.Application.Abstractions.PlatformConnections;

namespace Wasla.Application.PlatformConnections;

public sealed class CreatePlatformConnectionCommandValidator : AbstractValidator<CreatePlatformConnectionCommand>
{
    /// <summary>The longest API key or secret accepted, on create and when an edit replaces one.</summary>
    public const int MaxCredentialLength = 500;

    public CreatePlatformConnectionCommandValidator()
    {
        RuleFor(x => x.Platform)
            .IsInEnum();

        RuleFor(x => x.StoreId)
            .NotEmpty().WithMessage("Validation.StoreIdRequired")
            .MaximumLength(100).WithMessage("Validation.StoreIdMaxLength");

        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("Validation.ApiKeyRequired")
            .MaximumLength(MaxCredentialLength).WithMessage("Validation.ApiKeyMaxLength");

        RuleFor(x => x.ApiSecret)
            .NotEmpty().WithMessage("Validation.ApiSecretRequired")
            .MaximumLength(MaxCredentialLength).WithMessage("Validation.ApiSecretMaxLength");

        RuleFor(x => x.SupplierId)
            .MaximumLength(100).WithMessage("Validation.SupplierIdMaxLength")
            .When(x => !string.IsNullOrWhiteSpace(x.SupplierId));

        RuleFor(x => x.ExecutorEmail)
            .MaximumLength(200).WithMessage("Validation.ExecutorEmailMaxLength")
            .EmailAddress().WithMessage("Validation.ExecutorEmailInvalid")
            .When(x => !string.IsNullOrWhiteSpace(x.ExecutorEmail));
    }
}

