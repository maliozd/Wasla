using FluentValidation;
using Wasla.Application.Abstractions.PlatformConnections;

namespace Wasla.Application.PlatformConnections;

public sealed class CreatePlatformConnectionCommandValidator : AbstractValidator<CreatePlatformConnectionCommand>
{
    /// <summary>The longest API key or secret accepted, on create and when an edit replaces one.</summary>
    public const int MaxCredentialLength = 500;

    /// <summary>The longest supplier id stored, on create and edit: the length of the <c>SupplierId</c> column.</summary>
    public const int MaxSupplierIdLength = 64;

    /// <summary>
    /// Whether a supplier id is too long as it is stored: trimmed, and longer than <see cref="MaxSupplierIdLength"/>.
    /// A null or blank value is stored as null and is never too long.
    /// </summary>
    public static bool IsSupplierIdTooLong(string? supplierId) =>
        !string.IsNullOrWhiteSpace(supplierId) && supplierId.Trim().Length > MaxSupplierIdLength;

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
            .Must(supplierId => !IsSupplierIdTooLong(supplierId)).WithMessage("Validation.SupplierIdMaxLength");

        RuleFor(x => x.ExecutorEmail)
            .MaximumLength(200).WithMessage("Validation.ExecutorEmailMaxLength")
            .EmailAddress().WithMessage("Validation.ExecutorEmailInvalid")
            .When(x => !string.IsNullOrWhiteSpace(x.ExecutorEmail));
    }
}

