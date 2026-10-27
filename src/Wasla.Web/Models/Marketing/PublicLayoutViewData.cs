namespace Wasla.Web.Models.Marketing;

/// <summary><c>ViewData</c> keys a public page sets for <c>_PublicLayout</c> and its header.</summary>
public static class PublicLayoutViewData
{
    /// <summary>
    /// <c>true</c> when the page starts with a full-bleed photo hero: the header is laid over the photo, transparent and
    /// light, instead of the solid header the other public pages use.
    /// </summary>
    public const string HeaderOverlay = "PublicHeaderOverlay";
}
