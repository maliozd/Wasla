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
    /// <item>The primary handler is an explicitly constructed <see cref="SocketsHttpHandler"/>, so the handler type does
    /// not depend on the factory default of the running .NET version.</item>
    /// <item><see cref="ProviderPrimaryHandlerGuard"/> applies the cookie and redirect rules after every other handler
    /// configuration, so a later registration for the same name cannot turn them back on.</item>
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

        // First in the filter list, so it wraps every other filter and runs after all of them: IHttpClientFactory
        // applies filters[0] outermost.
        if (!services.Any(d => d.ServiceType == typeof(IHttpMessageHandlerBuilderFilter)
                               && d.ImplementationType == typeof(ProviderPrimaryHandlerGuard)))
        {
            services.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, ProviderPrimaryHandlerGuard>());
        }

        services.AddOptions<HttpClientFactoryOptions>(name)
            .Validate(
                static options => options.HttpClientActions.Count == 1,
                $"Provider HTTP client '{name}' must be configured only by its own provider registration.")
            .ValidateOnStart();

        return services.AddHttpClient(name, configureClient)
            .ConfigurePrimaryHttpMessageHandler(static () => CreatePrimaryHandler());
    }

    /// <summary>
    /// The provider primary handler. Apart from cookies and redirects it keeps the <see cref="SocketsHttpHandler"/>
    /// defaults, which on .NET 8 are also what the factory's default <see cref="HttpClientHandler"/> uses internally:
    /// the system proxy, standard certificate validation, no decompression, no connection limit and pooled connections
    /// that live as long as the handler (the factory replaces the handler every two minutes).
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false
    };

    /// <summary>A registered provider client name.</summary>
    internal sealed record ProviderHttpClientName(string Name);

    /// <summary>
    /// Runs after every handler configuration of a provider client (including <c>ConfigurePrimaryHttpMessageHandler</c>
    /// or <c>ConfigureHttpClientDefaults</c> calls made later by a host, and later filters) and turns cookies and
    /// redirects off on whatever primary handler they left. A handler type whose cookie and redirect behaviour it
    /// cannot control fails the client instead.
    /// </summary>
    internal sealed class ProviderPrimaryHandlerGuard(IEnumerable<ProviderHttpClientName> providerClients)
        : IHttpMessageHandlerBuilderFilter
    {
        private readonly HashSet<string> _names = providerClients.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);

            if (builder.Name is null || !_names.Contains(builder.Name))
                return;

            switch (builder.PrimaryHandler)
            {
                case SocketsHttpHandler sockets:
                    sockets.UseCookies = false;
                    sockets.AllowAutoRedirect = false;
                    break;
                case HttpClientHandler client:
                    client.UseCookies = false;
                    client.AllowAutoRedirect = false;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Provider HTTP client '{builder.Name}' needs a {nameof(SocketsHttpHandler)} or {nameof(HttpClientHandler)} primary handler, not {builder.PrimaryHandler.GetType().Name}.");
            }
        };
    }
}
