namespace Wasla.Application.Abstractions.Notifications;

public interface IUserNotificationSettingsService
{
    Task<GetNotificationSettingsResult> GetAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task UpdateAsync(Guid tenantId, Guid userId, UpdateNotificationSettingsCommand command, CancellationToken ct);
}

