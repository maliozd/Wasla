namespace Wasla.Application.Abstractions.Platform;

/// <summary>
/// A connection's provider credentials in plain text, for one request or one fetch. Each provider names the pair its
/// own way: Trendyol GO's API key and secret, Yemeksepeti's OAuth client id and secret. Never log or keep them; their
/// text form shows neither value.
/// </summary>
public sealed class ProviderCredentials
{
    public ProviderCredentials(string apiKey, string apiSecret)
    {
        ApiKey = apiKey;
        ApiSecret = apiSecret;
    }

    public string ApiKey { get; }

    public string ApiSecret { get; }

    public override string ToString() => "ProviderCredentials { ApiKey = [redacted], ApiSecret = [redacted] }";
}
