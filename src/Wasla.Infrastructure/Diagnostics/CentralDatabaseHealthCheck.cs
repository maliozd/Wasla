using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Diagnostics;

/// <summary>
/// Readiness probe for the shared central database. It does not open tenant databases
/// and does not call food-platform APIs.
/// </summary>
public sealed class CentralDatabaseHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CentralDatabaseHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var canConnect = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Central database is unreachable.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Central database is unreachable.");
        }
    }
}
