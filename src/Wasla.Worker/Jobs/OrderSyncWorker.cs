using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Demos;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.Worker.Console;

namespace Wasla.Worker.Jobs;

public sealed class OrderSyncWorker : BackgroundService
{
    private const int CycleIntervalSeconds = 15;
    private const int CatastrophicFailureBackoffSeconds = 30;
    private const int MaxParallelCustomers = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderSyncWorker> _logger;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly GuidedDemoSchedule _demoSchedule;
    private readonly OrderSyncCycleRunner _cycleRunner;

    public OrderSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OrderSyncWorker> logger,
        IConfiguration config,
        IHostEnvironment env,
        GuidedDemoSchedule demoSchedule,
        TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
        _env = env;
        _demoSchedule = demoSchedule;
        _cycleRunner = new OrderSyncCycleRunner(time, MaxParallelCustomers);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var providerMode = ProviderModeResolver.Resolve(_config);

        _logger.LogInformation(
            "Platform provider mode: {ProviderMode}. ConfigKey=Platforms:ProviderMode. Environment={EnvironmentName}",
            providerMode.ModeLabel,
            _env.EnvironmentName);

        if (providerMode.IsMock)
        {
            _logger.LogInformation(
                "Mock provider clients active for Yemeksepeti, GetirYemek and TrendyolYemek. Real platform APIs will not be called.");
        }
        else
        {
            _logger.LogWarning(
                "Real provider mode active. TrendyolYemek uses Trendyol GO HTTP client; Yemeksepeti uses the Yemeksepeti Partner API HTTP client; GetirYemek still uses a mock client.");
        }

        if (_env.IsDevelopment())
        {
            WorkerConsole.WriteStartupBanner(providerMode.ModeLabel, _env.EnvironmentName, CycleIntervalSeconds);
        }
        else
        {
            _logger.LogInformation("OrderSyncWorker started");
        }

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
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync cycle failed catastrophically, will retry in {BackoffSeconds}s", CatastrophicFailureBackoffSeconds);
                if (_env.IsDevelopment())
                    WorkerConsole.WriteError($"Cycle failed — retrying in {CatastrophicFailureBackoffSeconds}s");

                if (!await SafeDelayAsync(TimeSpan.FromSeconds(CatastrophicFailureBackoffSeconds), stoppingToken))
                    break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var swCycle = Stopwatch.StartNew();
        var cycleStartUtc = DateTime.UtcNow;

        var customers = await LoadActiveCustomersAsync(stoppingToken);

        if (_env.IsDevelopment())
        {
            WorkerConsole.WriteCycleStarted(cycleStartUtc, customers.Count, CycleIntervalSeconds);
        }
        else
        {
            _logger.LogDebug(
                "Order sync cycle started. StartedAtUtc={StartedAtUtc}, IntervalSeconds={IntervalSeconds}, ActiveCustomers={CustomerCount}",
                cycleStartUtc,
                CycleIntervalSeconds,
                customers.Count);
        }

        // Phase 1: every tenant's current window. Phase 2: bounded, round-robin history recovery.
        var byId = customers.ToDictionary(c => c.Id);
        var cycle = await _cycleRunner.RunAsync(
            customers.Select(c => c.Id).ToList(),
            (tenantId, ct) => SyncSingleCustomerAsync(byId[tenantId], ct),
            (tenantId, ct) => BackfillSingleCustomerAsync(byId[tenantId], ct),
            stoppingToken);

        swCycle.Stop();

        LogCycleTotals(swCycle.ElapsedMilliseconds, customers.Count, cycle.Current.Concat(cycle.Backfill).ToList());

        var remaining = TimeSpan.FromSeconds(CycleIntervalSeconds) - swCycle.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, stoppingToken);
    }

    private async Task<List<ActiveCustomer>> LoadActiveCustomersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

        return await central.Tenants
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new ActiveCustomer(c.Id, c.Slug, c.Name))
            .ToListAsync(ct);
    }

    private async Task<OrderSyncCustomerResult?> SyncSingleCustomerAsync(ActiveCustomer customer, CancellationToken ct)
    {
        OrderSyncCustomerResult? result = null;
        using var activity = new Activity("Wasla.OrderSync");
        activity.SetTag("tenant.id", customer.Id.ToString("D"));
        activity.Start();
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["TraceId"] = activity.TraceId.ToString(),
            ["TenantId"] = customer.Id.ToString("D")
        });

        using var scope = _scopeFactory.CreateScope();
        var syncer = scope.ServiceProvider.GetRequiredService<IOrderSyncService>();

        try
        {
            _logger.LogDebug(
                "Starting sync for customer {CustomerId} ({CustomerSlug}). CustomerName={CustomerName}",
                customer.Id,
                customer.Slug,
                customer.Name);

            var r = await syncer.SyncCustomerWithResultAsync(customer.Id, ct);
            result = r;

            if (_env.IsDevelopment())
                WorkerConsole.WriteCustomerResult(customer.Name, r.WasSyncDisabled, r.Connections);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One customer's failure must not stop the others in the same cycle.
            _logger.LogError(
                ex,
                "Sync failed for customer {CustomerId} ({CustomerSlug}). CustomerName={CustomerName}",
                customer.Id,
                customer.Slug,
                customer.Name);

            if (_env.IsDevelopment())
                WorkerConsole.WriteError($"Sync failed for {customer.Name} ({customer.Slug})");
        }

        await DeliverGuidedDemosAsync(scope, customer, ct);
        return result;
    }

    /// <summary>One history-recovery turn for a tenant whose current pass reported a backlog.</summary>
    private async Task<OrderSyncCustomerResult?> BackfillSingleCustomerAsync(ActiveCustomer customer, CancellationToken ct)
    {
        using var activity = new Activity("Wasla.OrderSync.Backfill");
        activity.SetTag("tenant.id", customer.Id.ToString("D"));
        activity.Start();
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["TraceId"] = activity.TraceId.ToString(),
            ["TenantId"] = customer.Id.ToString("D")
        });

        using var scope = _scopeFactory.CreateScope();
        var syncer = scope.ServiceProvider.GetRequiredService<IOrderSyncService>();

        try
        {
            return await syncer.BackfillCustomerAsync(customer.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One customer's failure must not stop the others in the same cycle.
            _logger.LogError(
                ex,
                "Order history recovery failed for customer {CustomerId} ({CustomerSlug}). CustomerName={CustomerName}",
                customer.Id,
                customer.Slug,
                customer.Name);
            return null;
        }
    }

    /// <summary>
    /// Plays the platform courier for guided demos in this cycle. Runs even when the tenant's
    /// sync is disabled or has no connection yet (onboarding tenants), and only touches demo sessions.
    /// It also hands the tenant's next demo deadline to <see cref="GuidedDemoScheduler"/>, which moves the practice
    /// order on time between cycles (and this first cycle catches up overdue stages after a restart). The cycle's own
    /// timing does not change.
    /// </summary>
    private async Task DeliverGuidedDemosAsync(IServiceScope scope, ActiveCustomer customer, CancellationToken ct)
    {
        try
        {
            var simulator = scope.ServiceProvider.GetRequiredService<IGuidedDemoDeliverySimulator>();
            var result = await simulator.AdvanceDueAndPlanAsync(customer.Id, ct);
            _demoSchedule.Plan(customer.Id, result.NextCheckAtUtc);
            if (result.Advanced > 0)
                _logger.LogDebug("Guided demo courier simulated for {CustomerSlug}. Advanced={Advanced}", customer.Slug, result.Advanced);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A training convenience must never disturb order synchronization.
            _logger.LogWarning(
                "Guided demo delivery simulation failed for {CustomerSlug}: {ExceptionType}",
                customer.Slug,
                ex.GetType().Name);
        }
    }

    private void LogCycleTotals(
        long elapsedMs,
        int customerCount,
        IEnumerable<OrderSyncCustomerResult> results)
    {
        var totals = new OrderSyncCycleTotals(
            CustomerCount: customerCount,
            ConnectionCount: results.Sum(r => r.ConnectionCount),
            FetchedCount: results.Sum(r => r.FetchedCount),
            InsertedCount: results.Sum(r => r.InsertedCount),
            UpdatedCount: results.Sum(r => r.UpdatedCount),
            SkippedCount: results.Sum(r => r.SkippedCount),
            UnchangedCount: results.Sum(r => r.UnchangedCount),
            FailedConnections: results.Sum(r => r.FailedConnections));

        if (_env.IsDevelopment())
        {
            WorkerConsole.WriteCycleCompleted(
                totals.CustomerCount,
                totals.ConnectionCount,
                totals.FetchedCount,
                totals.InsertedCount,
                totals.UpdatedCount,
                totals.UnchangedCount,
                totals.FailedConnections,
                elapsedMs);
        }
        else
        {
            var level = totals.FailedConnections > 0 ? LogLevel.Warning : LogLevel.Debug;
            _logger.Log(
                level,
                "Order sync cycle completed in {ElapsedMs} ms. Customers={CustomerCount}, Connections={ConnectionCount}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, FailedConnections={FailedConnectionCount}",
                elapsedMs,
                totals.CustomerCount,
                totals.ConnectionCount,
                totals.FetchedCount,
                totals.InsertedCount,
                totals.UpdatedCount,
                totals.SkippedCount,
                totals.UnchangedCount,
                totals.FailedConnections);
        }
    }

    private static async Task<bool> SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed record ActiveCustomer(Guid Id, string Slug, string Name);
}
