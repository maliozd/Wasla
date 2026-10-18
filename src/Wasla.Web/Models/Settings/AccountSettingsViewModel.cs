namespace Wasla.Web.Models.Settings;

public sealed class AccountSettingsViewModel
{
    /// <summary>The versioned URL of the current restaurant logo, or null when the tenant has none.</summary>
    public string? RestaurantLogoUrl { get; init; }

    public bool HasRestaurantLogo => RestaurantLogoUrl is not null;
}
