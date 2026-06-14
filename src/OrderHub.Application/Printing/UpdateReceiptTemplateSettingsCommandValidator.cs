using FluentValidation;
using OrderHub.Application.Abstractions.Printing;

namespace OrderHub.Application.Printing;

public sealed class UpdateReceiptTemplateSettingsCommandValidator
    : AbstractValidator<UpdateReceiptTemplateSettingsCommand>
{
    public UpdateReceiptTemplateSettingsCommandValidator()
    {
        RuleFor(x => x.ReceiptLanguage)
            .Must(v => string.IsNullOrWhiteSpace(v) || ReceiptLanguageCodes.IsSupported(v))
            .WithMessage("Settings.ReceiptLanguage.Invalid");

        RuleFor(x => x.ReceiptHeaderText)
            .Must(v => string.IsNullOrWhiteSpace(v)
                || (ReceiptTemplateSettings.NormalizeSingleLine(v, ReceiptTemplateLimits.HeaderMaxLength)?.Length ?? 0) <= ReceiptTemplateLimits.HeaderMaxLength)
            .WithMessage("Settings.ReceiptMessages.HeaderTooLong");

        RuleFor(x => x.ReceiptFooterText)
            .Must(v => v == null || (ReceiptTemplateSettings.NormalizeFooter(v)?.Length ?? 0) <= ReceiptTemplateLimits.FooterMaxLength)
            .WithMessage("Settings.ReceiptMessages.FooterTooLong");
    }
}
