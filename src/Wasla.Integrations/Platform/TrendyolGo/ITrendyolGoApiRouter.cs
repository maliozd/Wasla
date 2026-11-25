namespace Wasla.Integrations.Platform.TrendyolGo;

/// <summary>
/// Decides which Trendyol GO API one connection's requests go to: the deployment's <see cref="TrendyolGoOptions.BaseUrl"/>
/// or another API the host routes that connection to. The client asks before every request and before it reads the
/// connection's credentials, so a connection the router refuses sends no request and has no credential read. The host owns
/// the rule (Orders: the Trendyol GO Meal webhook pilot's stage routing, WAS-233). A host that registers none gets
/// <see cref="TrendyolGoDeploymentApiRouter"/>.
/// </summary>
public interface ITrendyolGoApiRouter
{
    /// <summary>
    /// The base address of the API <paramref name="connection"/>'s requests go to, absolute and ending in <c>/</c>, or
    /// null for the deployment's <see cref="TrendyolGoOptions.BaseUrl"/>. Throws to refuse the connection: then no API is
    /// called for it.
    /// </summary>
    Uri? ApiFor(ProviderConnectionContext connection);
}

/// <summary>Sends every connection to the deployment's <see cref="TrendyolGoOptions.BaseUrl"/>.</summary>
public sealed class TrendyolGoDeploymentApiRouter : ITrendyolGoApiRouter
{
    public Uri? ApiFor(ProviderConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return null;
    }
}
