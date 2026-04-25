using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Worker.Jobs;

public sealed class OrderSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderSyncWorker> _logger;

    public OrderSyncWorker(IServiceScopeFactory scopeFactory, ILogger<OrderSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OrderSyncWorker started");
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OrderSyncWorker stopped");
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cycleStart = DateTime.UtcNow;

                using var outerScope = _scopeFactory.CreateScope();
                var central = outerScope.ServiceProvider.GetRequiredService<CentralDbContext>();

                var customerIds = await central.Customers
                    .Where(c => c.IsActive)
                    .Select(c => c.Id)
                    .ToListAsync(stoppingToken);

                _logger.LogInformation("Sync cycle started, {Count} active customers", customerIds.Count);

                await Parallel.ForEachAsync(
                    customerIds,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 5,
                        CancellationToken = stoppingToken
                    },
                    async (customerId, innerCt) =>
                    {
                        using var innerScope = _scopeFactory.CreateScope();
                        var syncer = innerScope.ServiceProvider.GetRequiredService<IOrderSyncService>();
                        try
                        {
                            await syncer.SyncCustomerAsync(customerId, innerCt);
                        }
                        catch (OperationCanceledException) when (innerCt.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Sync failed for customer {CustomerId}", customerId);
                            // swallow — one customer's failure must not stop others
                        }
                    });

                var elapsed = DateTime.UtcNow - cycleStart;
                _logger.LogInformation("Sync cycle completed in {Elapsed}", elapsed);
                var remaining = TimeSpan.FromSeconds(15) - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync cycle failed catastrophically, will retry in 30s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

