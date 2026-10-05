namespace Wasla.Web.Models.Ui;

/// <summary>
/// The canonical Wasla logo (<c>wwwroot/images/brand/wasla-logo.svg</c>), rendered by the <c>_WaslaLogo</c> partial.
/// </summary>
public sealed class WaslaLogoModel
{
    /// <summary>Placement class that sets the logo height, for example <c>wasla-logo--sidebar</c>.</summary>
    public string? CssClass { get; init; }

    /// <summary>
    /// True when the logo sits inside something that is already named or hidden from assistive technology,
    /// so it gets an empty alt instead of announcing "Wasla" a second time.
    /// </summary>
    public bool Decorative { get; init; }
}
