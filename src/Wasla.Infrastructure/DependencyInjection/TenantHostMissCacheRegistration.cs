using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Tenant;

namespace Wasla.Infrastructure.DependencyInjection;

public static class TenantHostMissCacheRegistration
{
    /// <summary>
    /// The unknown-host cache tenant resolution uses in Wasla.Web and Wasla.Api (<see cref="TenantHostMissCache"/>),
    /// bound from <c>TenantResolution:MissCache</c> and validated at startup.
    /// </summary>
    public static IServiceCollection AddWaslaTenantHostMissCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TenantHostMissCacheOptions>()
            .Bind(configuration.GetSection(TenantHostMissCacheOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TenantHostMissCacheOptions>, TenantHostMissCacheOptionsValidator>());
        services.TryAddSingleton<TenantHostMissCache>();
        return services;
    }
}
