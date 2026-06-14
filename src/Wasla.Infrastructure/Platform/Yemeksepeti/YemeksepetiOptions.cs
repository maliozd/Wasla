namespace Wasla.Infrastructure.Platform.Yemeksepeti;

/// <summary>
/// Configuration for the Yemeksepeti Partner Picking Orders API.
/// Bind from appsettings: Platform:Yemeksepeti
/// </summary>
public sealed class YemeksepetiOptions
{
    public const string SectionName = "Platform:Yemeksepeti";

    /// <summary>Base URL of the Yemeksepeti partner API.</summary>
    public string BaseUrl { get; set; } = "https://yemeksepeti.partner.deliveryhero.io";

    /// <summary>Path used to acquire OAuth2 client_credentials tokens.</summary>
    public string TokenPath { get; set; } = "/v2/oauth/token";

    /// <summary>Default number of orders to request per page.</summary>
    public int DefaultPageSize { get; set; } = 20;

    /// <summary>HTTP request timeout for API calls.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
