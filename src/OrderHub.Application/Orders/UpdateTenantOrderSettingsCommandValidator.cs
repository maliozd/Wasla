using FluentValidation;
using OrderHub.Application.Abstractions.Orders;

namespace OrderHub.Application.Orders;

public sealed class UpdateTenantOrderSettingsCommandValidator : AbstractValidator<UpdateTenantOrderSettingsCommand>
{
    public UpdateTenantOrderSettingsCommandValidator()
    {
        RuleFor(x => x.ReceiptPrintCopyCount)
            .InclusiveBetween(1, 3)
            .WithMessage("Orders.ReceiptPrintCopyCountOutOfRange");
    }
}
