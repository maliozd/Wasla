using FluentValidation;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Notifications;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class UserNotificationSettingsService : IUserNotificationSettingsService
{
    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly IValidator<UpdateNotificationSettingsCommand> _validator;

    public UserNotificationSettingsService(
        ICustomerDbContextFactory dbFactory,
        IValidator<UpdateNotificationSettingsCommand> validator)
    {
        _dbFactory = dbFactory;
        _validator = validator;
    }

    public async Task<GetNotificationSettingsResult> GetAsync(Guid customerId, Guid userId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.UserNotificationSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new GetNotificationSettingsResult
            {
                NewOrderSoundEnabled = true,
                NewOrderSoundName = "bell1",
                NewOrderSoundRepeatCount = 3,
                NewOrderSoundVolume = 1.0m,
                ShowBrowserNotification = false
            };
        }

        return new GetNotificationSettingsResult
        {
            NewOrderSoundEnabled = row.NewOrderSoundEnabled,
            NewOrderSoundName = NormalizeSoundName(row.NewOrderSoundName),
            NewOrderSoundRepeatCount = Math.Clamp(row.NewOrderSoundRepeatCount, 1, 3),
            NewOrderSoundVolume = ClampVolume(row.NewOrderSoundVolume),
            ShowBrowserNotification = row.ShowBrowserNotification
        };
    }

    public async Task UpdateAsync(Guid customerId, Guid userId, UpdateNotificationSettingsCommand command, CancellationToken ct)
    {
        var validation = await _validator.ValidateAsync(command, ct).ConfigureAwait(false);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.UserNotificationSettings
            .FirstOrDefaultAsync(x => x.UserId == userId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new OrderHub.Domain.Entities.Customer.UserNotificationSettings
            {
                UserId = userId,
                NewOrderSoundEnabled = command.NewOrderSoundEnabled,
                NewOrderSoundName = NormalizeSoundName(command.NewOrderSoundName),
                NewOrderSoundRepeatCount = Math.Clamp(command.NewOrderSoundRepeatCount, 1, 3),
                NewOrderSoundVolume = ClampVolume(command.NewOrderSoundVolume),
                ShowBrowserNotification = command.ShowBrowserNotification,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserNotificationSettings.Add(row);
        }
        else
        {
            row.NewOrderSoundEnabled = command.NewOrderSoundEnabled;
            row.NewOrderSoundName = NormalizeSoundName(command.NewOrderSoundName);
            row.NewOrderSoundRepeatCount = Math.Clamp(command.NewOrderSoundRepeatCount, 1, 3);
            row.NewOrderSoundVolume = ClampVolume(command.NewOrderSoundVolume);
            row.ShowBrowserNotification = command.ShowBrowserNotification;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string NormalizeSoundName(string? name)
    {
        var v = (name ?? string.Empty).Trim().ToLowerInvariant();
        return NotificationSoundOptions.AllowedNames.Contains(v) ? v : "bell1";
    }

    private static decimal ClampVolume(decimal v)
    {
        if (v < 0m) return 0m;
        if (v > 1m) return 1m;
        return v;
    }
}

