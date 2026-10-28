namespace Wasla.Web.Models.Marketing;

/// <summary>Visual hierarchy of a marketing call to action (Figma "Button" component).</summary>
public enum MarketingLinkStyle
{
    Primary,
    Secondary,
    SecondaryOnDark,
    TertiaryOnDark
}

/// <summary>
/// A marketing call to action. Every target is an existing route, an in-page anchor or a confirmed external page; an
/// external page opens in a new tab.
/// </summary>
public sealed record MarketingLink(string Text, string Href, MarketingLinkStyle Style, bool External = false);

/// <summary>The dark closing panel shared by the homepage and the Wasla POS page.</summary>
public sealed record MarketingCtaPanelModel(string Id, string Title, string Lead, IReadOnlyList<MarketingLink> Actions);

/// <summary>A food-delivery platform mark inside a decorative mockup.</summary>
public sealed record MarketingPlatformMarkModel(string Platform, string? CssClass = null);
