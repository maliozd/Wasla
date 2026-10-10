using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Platform;

/// <summary>
/// Registers the <see cref="HttpClient"/> of a real food-platform provider. Every real provider client must be
/// registered here, under its own name, so that it gets its own base address, timeout, handler pool and log category
/// (<c>System.Net.Http.HttpClient.{name}.*</c>) and the shared HTTP rules below (WAS-95, WAS-97).
/// <para>
/// Provider handlers are pooled by <see cref="IHttpClientFactory"/> and shared by every tenant (the Yemeksepeti client is
/// a singleton), so they hold no connection state. They never store or send cookies: a cookie set by one connection's
/// response would otherwise go out with every later request to that host, whichever tenant sent it. They never follow
/// redirects either: a followed redirect re-sends the request to whatever URL the response names, including the
/// Yemeksepeti token request's client secret on a 307 or 308. A 3xx response reaches the client, which fails it like any
/// other unsuccessful status. Credentials are set on each request.
/// </para>
/// </summary>
internal static class ProviderHttpClientRegistration
{
    /// <summary>
    /// Adds the named provider client. Throws if a provider client with the same name is already registered.
    /// <list type="bullet">
    /// <item><paramref name="configureClient"/> must be the only client configuration for the name. Another registration
    /// that configures the same client (a second <c>AddHttpClient</c> with this name, or
    /// <c>ConfigureHttpClientDefaults</c>) fails validation, at startup in the hosts and otherwise when the client is
    /// first created, instead of silently overriding the base address.</item>
    /// </list>
    /// </summary>
    public static IHttpClientBuilder AddProviderHttpClient(
        this IServiceCollection services,
        string name,
        Action<IServiceProvider, HttpClient> configureClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configureClient);

        if (services.Any(d => d.ServiceType == typeof(ProviderHttpClientName)
                              && d.ImplementationInstance is ProviderHttpClientName registered
                              && string.Equals(registered.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"A provider HTTP client named '{name}' is already registered. Every provider needs its own client name.");
        }

        services.AddSingleton(new ProviderHttpClientName(name));

        services.AddOptions<HttpClientFactoryOptions>(name)
            .Validate(
                static options => options.HttpClientActions.Count == 1,
                $"Provider HTTP client '{name}' must be configured only by its own provider registration.")
            .ValidateOnStart();

        return services.AddHttpClient(name, configureClient)
            .ConfigurePrimaryHttpMessageHandler(static (handler, _) =>
            {
                if (handler is not HttpClientHandler primary)
                {
                    throw new InvalidOperationException(
                        $"Provider HTTP clients expect an {nameof(HttpClientHandler)} primary handler, not {handler.GetType().Name}.");
                }

                primary.UseCookies = false;
                primary.AllowAutoRedirect = false;
            });
    }

    /// <summary>A registered provider client name.</summary>
    internal sealed record ProviderHttpClientName(string Name);
}
