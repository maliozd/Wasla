using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Settings;

public sealed class AccountSettingsViewModel
{
    /// <summary>The restaurant name, shown beside the logo in the Live Screen previews.</summary>
    public string RestaurantName { get; init; } = string.Empty;

    /// <summary>The versioned URL of the current restaurant logo, or null when the tenant has none.</summary>
    public string? RestaurantLogoUrl { get; init; }

    /// <summary>What the Live Screen header draws behind the current logo.</summary>
    public RestaurantLogoBackground RestaurantLogoBackground { get; init; } = RestaurantLogoBackground.ThemeAware;

    public bool HasRestaurantLogo => RestaurantLogoUrl is not null;
}
