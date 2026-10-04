using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;

namespace Wasla.Infrastructure.Diagnostics;

public static class WaslaHealthCheckExtensions
{
    public static IHealthChecksBuilder AddWaslaHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<CentralDatabaseHealthCheck>("central-db", tags: ["ready"]);

    public static void MapWaslaHealthChecks(this WebApplication app)
    {
        // Liveness only proves the process can answer. No dependency checks.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = static _ => false
        });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = static check => check.Tags.Contains("ready")
        });
    }
}
