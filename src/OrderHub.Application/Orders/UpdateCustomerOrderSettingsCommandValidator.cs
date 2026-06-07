using FluentValidation;
using OrderHub.Application.Abstractions.Orders;

namespace OrderHub.Application.Orders;

public sealed class UpdateCustomerOrderSettingsCommandValidator : AbstractValidator<UpdateCustomerOrderSettingsCommand>
{
    public UpdateCustomerOrderSettingsCommandValidator()
    {
        RuleFor(x => x.ReceiptPrintCopyCount)
            .InclusiveBetween(1, 3)
            .WithMessage("Orders.ReceiptPrintCopyCountOutOfRange");
    }
}
