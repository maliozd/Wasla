namespace Wasla.Web.Models.Marketing;

/// <summary>
/// Search and sharing metadata of a public marketing page, rendered by <c>_PublicLayout</c>: the document title, the
/// meta and Open Graph description, and the canonical path (combined with the public origin by
/// <see cref="Routing.PublicSiteUrls.BuildCanonicalUrl"/>).
/// </summary>
public sealed record PublicPageMetadata(string Title, string Description, string CanonicalPath)
{
    /// <summary>The <c>ViewData</c> key under which a view hands its metadata to the layout.</summary>
    public const string ViewDataKey = "PublicPageMetadata";
}
