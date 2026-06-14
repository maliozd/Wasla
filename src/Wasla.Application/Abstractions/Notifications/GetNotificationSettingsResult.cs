namespace Wasla.Application.Abstractions.Notifications;

public sealed class GetNotificationSettingsResult
{
    public bool NewOrderSoundEnabled { get; init; } = true;
    public string NewOrderSoundName { get; init; } = "bell1";
    public int NewOrderSoundRepeatCount { get; init; } = 3;
    public decimal NewOrderSoundVolume { get; init; } = 1.0m;
    public bool ShowBrowserNotification { get; init; } = false;

    public string NewOrderHighlightColor { get; init; } = "yellow";
    public string NewOrderHighlightBehavior { get; init; } = "fade";
    public int NewOrderHighlightDurationSeconds { get; init; } = 30;
}

