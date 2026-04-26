using FluentValidation;
using OrderHub.Application.Abstractions.Notifications;

namespace OrderHub.Application.Notifications;

public sealed class UpdateNotificationSettingsCommandValidator : AbstractValidator<UpdateNotificationSettingsCommand>
{
    private static readonly string[] AllowedSoundNames = ["bell", "chime", "alert"];

    public UpdateNotificationSettingsCommandValidator()
    {
        RuleFor(x => x.NewOrderSoundName)
            .NotEmpty().WithMessage("Zil sesi seçilmelidir.")
            .Must(v => AllowedSoundNames.Contains(v)).WithMessage("Geçersiz zil sesi seçimi.");

        RuleFor(x => x.NewOrderSoundRepeatCount)
            .InclusiveBetween(1, 5).WithMessage("Tekrar sayısı 1 ile 5 arasında olmalıdır.");

        RuleFor(x => x.NewOrderSoundVolume)
            .InclusiveBetween(0m, 1m).WithMessage("Ses seviyesi 0 ile 1 arasında olmalıdır.");
    }
}

