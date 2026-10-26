using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

public static class TrendyolGoMealWebhookPilotRegistration
{
    /// <summary>
    /// The pilot's options (validated at startup: an enabled pilot with an incomplete configuration, or next to the
    /// production Trendyol GO API, stops the host) and its inbox. Wasla.Api receives; the processor is registered by the
    /// Worker with <see cref="AddTrendyolGoMealWebhookPilotProcessing"/>.
    /// </summary>
    public static IServiceCollection AddTrendyolGoMealWebhookPilot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TrendyolGoMealWebhookPilotOptions>()
            .Bind(configuration.GetSection(TrendyolGoMealWebhookPilotOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TrendyolGoMealWebhookPilotOptions>, TrendyolGoMealWebhookPilotOptionsValidator>());
        services.TryAddScoped<TrendyolGoMealWebhookInbox>();
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
