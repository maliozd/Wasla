using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Security;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Services;

namespace Wasla.Cli;

/// <summary>
/// The services the CLI commands use once CentralDb is known to be configured, and the configuration sections they
/// bind. The tenant SQL Server is not bound here: provisioning reads <c>TenantDb:ServerInstance</c> when it runs.
/// </summary>
internal static class CliServices
{
    public static IServiceCollection AddWaslaCliServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CentralDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("CentralDb")));

        services.AddSingleton<ISecretManager, AesSecretManager>();
        services.Configure<TenantOnboardingOptions>(configuration.GetSection(TenantOnboardingOptions.SectionName));
        services.AddScoped<ITenantDatabaseProvisioningOperations, SqlServerTenantDatabaseProvisioningOperations>();
        services.AddScoped<IPendingRegistrationProvisioningService, PendingRegistrationProvisioningService>();
        services.AddWaslaEmail(configuration);
        return services;
    }
}
