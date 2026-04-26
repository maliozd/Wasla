using OrderHub.Domain.Common;

namespace OrderHub.Domain.Entities.Customer;

/// <summary>
/// Per-user notification preferences within a single CustomerDb.
/// Browser autoplay/unlock state is NOT stored here.
/// </summary>
public sealed class UserNotificationSettings : BaseEntity
{
    public Guid UserId { get; set; }
    public AppUser? AppUser { get; set; }

    public bool NewOrderSoundEnabled { get; set; } = true;
    public string NewOrderSoundName { get; set; } = "bell1";
    public int NewOrderSoundRepeatCount { get; set; } = 3;
    public decimal NewOrderSoundVolume { get; set; } = 1.0m;

    public bool ShowBrowserNotification { get; set; } = false;
}

