using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

public static class TrendyolGoMealWebhookPilotRegistration
{
    /// <summary>
    /// The pilot's options and its inbox. Wasla.Api receives; the processor is registered by the Worker with
    /// <see cref="AddTrendyolGoMealWebhookPilotProcessing"/>.
    /// </summary>
    public static IServiceCollection AddTrendyolGoMealWebhookPilot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTrendyolGoMealWebhookPilotOptions(configuration);
        services.TryAddScoped<TrendyolGoMealWebhookInbox>();
        return services;
    }

    /// <summary>
    /// Binds and validates the pilot's options once, at startup: an enabled pilot with an incomplete configuration, or a
    /// provider API override that is not exactly the stage API for one identified connection, stops the host. Every host
    /// that calls Trendyol GO registers them (through <c>AddWaslaInfrastructure</c>), so the pilot's connection is routed
    /// the same way in Web, Api and Worker.
    /// </summary>
    internal static IServiceCollection AddTrendyolGoMealWebhookPilotOptions(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IValidateOptions<TrendyolGoMealWebhookPilotOptions>)
                              && d.ImplementationType == typeof(TrendyolGoMealWebhookPilotOptionsValidator)))
        {
            return services;
        }

        services.AddOptions<TrendyolGoMealWebhookPilotOptions>()
            .Bind(configuration.GetSection(TrendyolGoMealWebhookPilotOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TrendyolGoMealWebhookPilotOptions>, TrendyolGoMealWebhookPilotOptionsValidator>();
        return services;
    }

    /// <summary>The pilot plus its processor, for the Worker. Needs <c>OrderSyncService</c> registered as itself.</summary>
    public static IServiceCollection AddTrendyolGoMealWebhookPilotProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTrendyolGoMealWebhookPilot(configuration);
        services.TryAddScoped<TrendyolGoMealWebhookProcessor>();
        return services;
    }
}
