using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.Worker.Jobs;

/// <summary>
/// Processes the Trendyol GO Meal webhook stage pilot's stored events every few seconds. Exits at once while the pilot
/// is off. Separate from the order sync cycle, which keeps polling on its own schedule: the shared ingestion path makes
/// the two safe to run side by side.
/// </summary>
public sealed class TrendyolGoMealWebhookWorker(
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<TrendyolGoMealWebhookPilotOptions> options,
    ILogger<TrendyolGoMealWebhookWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        logger.LogWarning(
            "Trendyol GO Meal webhook stage pilot is enabled for one tenant connection. TenantId={TenantId}, ConnectionId={ConnectionId}",
            options.Value.TenantId,
            options.Value.PlatformConnectionId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleDelay;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<TrendyolGoMealWebhookProcessor>();
                var processed = await processor.ProcessDueAsync(BatchSize, stoppingToken);
                if (processed == BatchSize)
                    delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trendyol GO Meal webhook processing loop failed; retrying shortly.");
                delay = ErrorDelay;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
