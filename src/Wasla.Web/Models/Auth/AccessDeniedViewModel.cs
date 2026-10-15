namespace Wasla.Web.Models.Auth;

public sealed class AccessDeniedViewModel
{
    /// <summary>The signed-in user's landing page (a page they may open), or null when there is none.</summary>
    public string? LandingPageUrl { get; init; }
}
