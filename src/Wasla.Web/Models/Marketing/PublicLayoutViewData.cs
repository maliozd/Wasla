namespace Wasla.Web.Models.Marketing;

/// <summary><c>ViewData</c> keys a public page sets for <c>_PublicLayout</c> and its header.</summary>
public static class PublicLayoutViewData
{
    /// <summary>
    /// <c>true</c> when the page starts with a full-bleed photo hero: the header is laid over the photo, transparent and
    /// light, instead of the solid header the other public pages use.
    /// </summary>
    public const string HeaderOverlay = "PublicHeaderOverlay";

    /// <summary>
    /// <c>true</c> on the coming-soon page: the whole page lies on its photo, so the header and footer are light on dark
    /// (<c>wm-site--coming-soon</c> on <c>body</c>).
    /// </summary>
    public const string ComingSoonPage = "PublicComingSoonPage";

    /// <summary>
    /// <c>true</c> on a page that draws its own header and footer, such as the pilot application (WAS-217): the layout
    /// renders neither the shared public header nor the footer (<c>wm-site--standalone</c> on <c>body</c>).
    /// </summary>
    public const string StandalonePage = "PublicStandalonePage";
}
