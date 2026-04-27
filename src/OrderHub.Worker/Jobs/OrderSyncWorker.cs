using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Worker.Jobs;

public sealed class OrderSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderSyncWorker> _logger;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;

    public OrderSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OrderSyncWorker> logger,
        IConfiguration config,
        IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
        _env = env;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var providerMode = (_config["Platforms:ProviderMode"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(providerMode))
        {
            // Backward compatible legacy flag.
            var legacy = _config.GetValue<bool?>("Platform:UseMocks");
            providerMode = legacy.HasValue ? (legacy.Value ? "Mock (legacy)" : "Real (legacy)") : "Mock (default)";
        }

        _logger.LogInformation(
            "Platform provider mode: {ProviderMode}. Environment={EnvironmentName}",
            providerMode,
            _env.EnvironmentName);

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
        const int cycleIntervalSeconds = 15;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var swCycle = Stopwatch.StartNew();
                var cycleStartUtc = DateTime.UtcNow;

                using var outerScope = _scopeFactory.CreateScope();
                var central = outerScope.ServiceProvider.GetRequiredService<CentralDbContext>();

                var customers = await central.Customers
                    .AsNoTracking()
                    .Where(c => c.IsActive)
                    .Select(c => new { c.Id, c.Slug, c.Name })
                    .ToListAsync(stoppingToken);

                _logger.LogInformation(
                    "Order sync cycle started. StartedAtUtc={StartedAtUtc}, IntervalSeconds={IntervalSeconds}, ActiveCustomers={CustomerCount}",
                    cycleStartUtc,
                    cycleIntervalSeconds,
                    customers.Count);

                var results = new ConcurrentBag<OrderSyncCustomerResult>();

                await Parallel.ForEachAsync(
                    customers,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 5,
                        CancellationToken = stoppingToken
                    },
                    async (customer, innerCt) =>
                    {
                        using var innerScope = _scopeFactory.CreateScope();
                        var syncer = innerScope.ServiceProvider.GetRequiredService<IOrderSyncService>();
                        try
                        {
                            _logger.LogInformation(
                                "Starting sync for customer {CustomerId} ({CustomerSlug}). CustomerName={CustomerName}",
                                customer.Id,
                                customer.Slug,
                                customer.Name);

                            var r = await syncer.SyncCustomerWithResultAsync(customer.Id, innerCt);
                            results.Add(r);
                        }
                        catch (OperationCanceledException) when (innerCt.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Sync failed for customer {CustomerId} ({CustomerSlug}). CustomerName={CustomerName}",
                                customer.Id,
                                customer.Slug,
                                customer.Name);
                            // swallow — one customer's failure must not stop others
                        }
                    });

                swCycle.Stop();

                var totals = new OrderSyncCycleTotals(
                    CustomerCount: customers.Count,
                    ConnectionCount: results.Sum(r => r.ConnectionCount),
                    FetchedCount: results.Sum(r => r.FetchedCount),
                    InsertedCount: results.Sum(r => r.InsertedCount),
                    UpdatedCount: results.Sum(r => r.UpdatedCount),
                    SkippedCount: results.Sum(r => r.SkippedCount),
                    UnchangedCount: results.Sum(r => r.UnchangedCount),
                    FailedConnections: results.Sum(r => r.FailedConnections));

                _logger.LogInformation(
                    "Order sync cycle completed in {ElapsedMs} ms. Customers={CustomerCount}, Connections={ConnectionCount}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, FailedConnections={FailedConnectionCount}",
                    swCycle.ElapsedMilliseconds,
                    totals.CustomerCount,
                    totals.ConnectionCount,
                    totals.FetchedCount,
                    totals.InsertedCount,
                    totals.UpdatedCount,
                    totals.SkippedCount,
                    totals.UnchangedCount,
                    totals.FailedConnections);

                var remaining = TimeSpan.FromSeconds(cycleIntervalSeconds) - TimeSpan.FromMilliseconds(swCycle.ElapsedMilliseconds);
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

