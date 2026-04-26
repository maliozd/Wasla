namespace OrderHub.Application.Abstractions.Notifications;

public sealed class UpdateNotificationSettingsCommand
{
    public bool NewOrderSoundEnabled { get; init; } = true;
    public string NewOrderSoundName { get; init; } = "bell";
    public int NewOrderSoundRepeatCount { get; init; } = 3;
    public decimal NewOrderSoundVolume { get; init; } = 1.0m;
    public bool ShowBrowserNotification { get; init; } = false;
}

