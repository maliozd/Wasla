namespace OrderHub.Web.Models.Notifications;

public sealed class NotificationSettingsViewModel
{
    public bool NewOrderSoundEnabled { get; set; } = true;
    public string NewOrderSoundName { get; set; } = "bell";
    public int NewOrderSoundRepeatCount { get; set; } = 3;
    public decimal NewOrderSoundVolume { get; set; } = 1.0m;
    public bool ShowBrowserNotification { get; set; } = false;

    public bool SoundUnlocked { get; set; } = false;
}

