using FluentValidation;
using Wasla.Application.Abstractions.Orders;

namespace Wasla.Application.Orders;

public sealed class UpdateTenantOrderSettingsCommandValidator : AbstractValidator<UpdateTenantOrderSettingsCommand>
{
    public UpdateTenantOrderSettingsCommandValidator()
    {
        RuleFor(x => x.ReceiptPrintCopyCount)
            .InclusiveBetween(1, 3)
            .WithMessage("Orders.ReceiptPrintCopyCountOutOfRange");
    }
}
