using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wasla.Integrations.Platform;
using Wasla.Integrations.Platform.Mock;
using Wasla.Integrations.Platform.TrendyolGo;
using Wasla.Integrations.Platform.Yemeksepeti;

namespace Wasla.Integrations.DependencyInjection;

public static class WaslaIntegrationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="IFoodPlatformClient"/> per food platform for the provider mode in
    /// <c>Platforms:ProviderMode</c>, read by <see cref="ProviderModeResolver"/>: a missing or invalid value throws.
    /// <list type="bullet">
    /// <item>Mock: the three mock clients. No real client, provider HTTP client or Trendyol GO rate limiter is registered,
    /// so no provider host is called.</item>
    /// <item>Real: the Yemeksepeti and Trendyol GO clients, each with its own named <see cref="HttpClient"/>
    /// (<see cref="ProviderHttpClientRegistration"/>), and one Trendyol GO rate limiter for the process. Getir stays a
    /// mock.</item>
    /// </list>
    /// The options bind <c>Platform:TrendyolGo</c> and <c>Platform:Yemeksepeti</c>. <see cref="TimeProvider.System"/> and
    /// the Trendyol GO API router (<see cref="TrendyolGoDeploymentApiRouter"/>) are added only when the host has not
    /// registered its own. The host supplies each connection's credentials with every call
    /// (<see cref="ProviderConnectionContext"/>); the library stores none.
    /// </summary>
    public static IServiceCollection AddWaslaIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<TrendyolGoOptions>(configuration.GetSection(TrendyolGoOptions.SectionName));
        services.Configure<YemeksepetiOptions>(configuration.GetSection(YemeksepetiOptions.SectionName));
        services.TryAddSingleton<ITrendyolGoApiRouter, TrendyolGoDeploymentApiRouter>();

        var providerMode = ProviderModeResolver.Resolve(configuration);

        if (providerMode.IsMock)
        {
            // Mock mode: all three platforms use mock clients. No real provider HTTP calls.
            services.AddSingleton<IFoodPlatformClient, MockYemeksepetiFoodPlatformClient>();
            services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();
            services.AddSingleton<IFoodPlatformClient, MockTrendyolYemekFoodPlatformClient>();
        }
        else
        {
            // Real mode:
            //   Yemeksepeti → real YemeksepetiFoodPlatformClient (OAuth2 + Partner Picking API)
            //   GetirYemek  → still mock (real client not yet implemented)
            //   TrendyolYemek → real TrendyolGoFoodPlatformClient (Basic auth + Trendyol GO API)
            // Each real provider has its own named HttpClient (see ProviderHttpClientRegistration).
            services.AddProviderHttpClient(YemeksepetiFoodPlatformClient.YemeksepetiHttpClientName, (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<YemeksepetiOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = opts.RequestTimeout;
            });
            services.AddSingleton<IFoodPlatformClient, YemeksepetiFoodPlatformClient>();

            services.AddSingleton<IFoodPlatformClient, MockGetirYemekFoodPlatformClient>();

            // One limiter per process: every Trendyol GO client instance (typed clients are transient) and every
            // tenant share its request budget.
            services.AddSingleton<TrendyolRequestRateLimiter>();
            services.AddProviderHttpClient(TrendyolGoFoodPlatformClient.TrendyolGoHttpClientName, (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<TrendyolGoOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = opts.RequestTimeout;
            }).AddTypedClient<IFoodPlatformClient, TrendyolGoFoodPlatformClient>();
        }

        return services;
    }
}
