using FluentValidation;
using OrderHub.Application.Abstractions.Notifications;

namespace OrderHub.Application.Notifications;

public sealed class UpdateNotificationSettingsCommandValidator : AbstractValidator<UpdateNotificationSettingsCommand>
{
    public UpdateNotificationSettingsCommandValidator()
    {
        RuleFor(x => x.NewOrderSoundName)
            .NotEmpty().WithMessage("Zil sesi seçilmelidir.")
            .Must(v => NotificationSoundOptions.AllowedNames.Contains(v)).WithMessage("Geçersiz zil sesi seçimi.");

        RuleFor(x => x.NewOrderSoundRepeatCount)
            .InclusiveBetween(1, 3).WithMessage("Tekrar sayısı 1 ile 3 arasında olmalıdır.");

        RuleFor(x => x.NewOrderSoundVolume)
            .InclusiveBetween(0m, 1m).WithMessage("Ses seviyesi 0 ile 1 arasında olmalıdır.");
    }
}

