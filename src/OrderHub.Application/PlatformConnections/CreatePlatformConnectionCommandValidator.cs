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
            .NotEmpty().WithMessage("StoreId is required.")
            .MaximumLength(100).WithMessage("StoreId max length is 100.");

        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("ApiKey is required.")
            .MaximumLength(500).WithMessage("ApiKey max length is 500.");

        RuleFor(x => x.ApiSecret)
            .NotEmpty().WithMessage("ApiSecret is required.")
            .MaximumLength(500).WithMessage("ApiSecret max length is 500.");
    }
}

