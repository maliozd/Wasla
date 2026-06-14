namespace Wasla.Application.Abstractions.Notifications;

public interface IUserNotificationSettingsService
{
    Task<GetNotificationSettingsResult> GetAsync(Guid customerId, Guid userId, CancellationToken ct);
    Task UpdateAsync(Guid customerId, Guid userId, UpdateNotificationSettingsCommand command, CancellationToken ct);
}

