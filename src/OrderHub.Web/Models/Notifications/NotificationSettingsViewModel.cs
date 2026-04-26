namespace OrderHub.Web.Models.Notifications;

public sealed class NotificationSettingsViewModel
{
    public bool NewOrderSoundEnabled { get; set; } = true;
    public string NewOrderSoundName { get; set; } = "bell1";
    public int NewOrderSoundRepeatCount { get; set; } = 3;
    public int NewOrderSoundVolumePercent { get; set; } = 100;
    public bool ShowBrowserNotification { get; set; } = false;

    public bool SoundUnlocked { get; set; } = false;

    public IReadOnlyList<SoundOption> AvailableSounds { get; set; } = Array.Empty<SoundOption>();

    public sealed class SoundOption
    {
        public required string Name { get; set; }
        public required string Label { get; set; }
        public required string Url { get; set; }
    }
}

