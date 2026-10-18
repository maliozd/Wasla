namespace Wasla.Domain.Enums;

/// <summary>
/// What the Live Screen header draws behind the restaurant logo (WAS-127). The Owner chooses it; the uploaded image is
/// never changed. <see cref="ThemeAware"/> is 0 on purpose: it is the default for every logo, including one saved before
/// the choice existed.
/// </summary>
public enum RestaurantLogoBackground
{
    /// <summary>Transparent in the light theme; a subtle light surface in the dark theme, so dark logos stay readable.</summary>
    ThemeAware = 0,

    /// <summary>No background in either theme.</summary>
    Transparent = 1,

    /// <summary>A subtle light surface in both themes.</summary>
    Light = 2,

    /// <summary>A subtle dark surface in both themes.</summary>
    Dark = 3
}
