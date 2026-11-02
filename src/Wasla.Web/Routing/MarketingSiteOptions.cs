namespace Wasla.Web.Routing;

/// <summary>
/// What the marketing host publishes (<c>Wasla:MarketingSite</c>). Until the company side is ready the host shows only
/// the coming-soon page at <c>/</c> (decision of 2026-10-28). The full site (the homepage, <c>/pos</c>, <c>/contact</c>
/// and <c>/privacy-notice</c>) stays in the code and is published by turning <see cref="FullSiteEnabled"/> on.
/// </summary>
public sealed class MarketingSiteOptions
{
    public const string SectionName = "Wasla:MarketingSite";

    /// <summary>
    /// Whether the full marketing site is published. When false (the default), <c>/</c> shows the coming-soon page, the
    /// other marketing pages answer 404 (<see cref="Security.FullMarketingSiteRequiredAttribute"/>), and the shared
    /// public header and footer link only to the homepage, sign-in, e-mail and Instagram.
    /// </summary>
    public bool FullSiteEnabled { get; set; }
}
