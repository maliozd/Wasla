using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mock;
using Wasla.Infrastructure.Realtime;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Wasla.Infrastructure.Sync;

public sealed class OrderSyncService : IOrderSyncService
{
    /// <summary>
    /// Backfill windows per connection in one <see cref="BackfillCustomerAsync"/> turn. One window per turn lets the
    /// cycle interleave tenants round-robin (<see cref="OrderSyncCycleRunner"/>).
    /// </summary>
    internal const int BackfillWindowsPerTurn = 1;

    private readonly ITenantDbContextFactory _customerDbFactory;
    private readonly IEnumerable<IFoodPlatformClient> _platformClients;
    private readonly OrderIngestion _ingestion;
    private readonly ILogger<OrderSyncService> _logger;
    private readonly ITenantBusinessSubtypeReader? _businessSubtypeReader;
    private readonly TimeProvider _time;

    private static readonly ResiliencePipeline<IReadOnlyCollection<ExternalOrderDto>> _fetchPipeline =
        new ResiliencePipelineBuilder<IReadOnlyCollection<ExternalOrderDto>>()
            .AddRetry(FetchRetry())
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();

    /// <summary>
    /// For clients that fetch by window (<see cref="IFoodPlatformClient.MaxFetchWindow"/>, Trendyol GO). They may wait
    /// for the process-wide request limiter, so a whole-fetch timeout would count queueing as a provider timeout. Each
    /// HTTP request keeps its own HttpClient timeout, and the page cap bounds the requests per window.
    /// </summary>
    private static readonly ResiliencePipeline<IReadOnlyCollection<ExternalOrderDto>> _windowedFetchPipeline =
        new ResiliencePipelineBuilder<IReadOnlyCollection<ExternalOrderDto>>()
            .AddRetry(FetchRetry())
            .Build();

    /// <summary>
    /// Retries transient fetch failures. A 429 is not retried here: the client already retried that page after the
    /// provider's delay, and repeating the whole window would multiply requests against an exhausted quota.
    /// </summary>
    private static RetryStrategyOptions<IReadOnlyCollection<ExternalOrderDto>> FetchRetry() => new()
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = new PredicateBuilder<IReadOnlyCollection<ExternalOrderDto>>()
            .Handle<HttpRequestException>(ex => ex.StatusCode != HttpStatusCode.TooManyRequests)
            .Handle<TimeoutException>()
            .Handle<TimeoutRejectedException>()
    };

    public OrderSyncService(
        ITenantDbContextFactory customerDbFactory,
        IEnumerable<IFoodPlatformClient> platformClients,
        IOrderStatusMapper statusMapper,
        IOrderAutoApproveService autoApprove,
        IOrderReceiptCreationService receiptCreation,
        ITenantChangeNotifier notifier,
        ILogger<OrderSyncService> logger,
        ITenantBusinessSubtypeReader? businessSubtypeReader = null,
        TimeProvider? time = null)
    {
        _customerDbFactory = customerDbFactory;
        _platformClients = platformClients;
        _ingestion = new OrderIngestion(statusMapper, autoApprove, receiptCreation, notifier, logger);
        _logger = logger;
        _businessSubtypeReader = businessSubtypeReader;
        _time = time ?? TimeProvider.System;
    }

    public async Task SyncCustomerAsync(Guid customerId, CancellationToken ct)
    {
        await SyncCustomerWithResultAsync(customerId, ct).ConfigureAwait(false);
    }

    public async Task<OrderSyncCustomerResult> SyncCustomerWithResultAsync(Guid customerId, CancellationToken ct)
    {
        var swCustomer = Stopwatch.StartNew();
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        if (!await IsOrderSyncEnabledAsync(db, ct).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Order sync skipped — tenant sync is disabled. CustomerId={CustomerId}",
                customerId);

            return new OrderSyncCustomerResult(customerId, 0, 0, 0, 0, 0, 0, 0) { WasSyncDisabled = true };
        }

        // Same clock as LastSyncAttempt and the fetch windows, so "behind" (checkpoint older than the last attempt) is exact.
        var now = _time.GetUtcNow().UtcDateTime;
        // Load all connections so we can log *why* a connection was skipped.
        var allConnections = await db.PlatformConnections.ToListAsync(ct).ConfigureAwait(false);
        var activeConnections = allConnections.Where(c => c.IsActive).ToList();

        var dueConnections = activeConnections
            .Where(c =>
                (c.CircuitOpenUntil == null || c.CircuitOpenUntil <= now) &&
                (c.LastSyncAttempt == null || c.LastSyncAttempt.Value.AddSeconds(c.SyncIntervalSeconds) <= now))
            .ToList();

        if (activeConnections.Count == 0)
        {
            _logger.LogInformation(
                "No active platform connections for customer {CustomerId}",
                customerId);
            return new OrderSyncCustomerResult(customerId, 0, 0, 0, 0, 0, 0, 0);
        }

        if (dueConnections.Count == 0)
        {
            _logger.LogDebug(
                "No due platform connections for customer {CustomerId}. ActiveConnections={ActiveConnectionCount}",
                customerId,
                activeConnections.Count);
            return new OrderSyncCustomerResult(customerId, activeConnections.Count, 0, 0, 0, 0, 0, 0);
        }

        foreach (var c in activeConnections)
        {
            if (c.CircuitOpenUntil != null && c.CircuitOpenUntil > now)
            {
                _logger.LogInformation(
                    "Skipping platform connection {ConnectionId} because circuit is open until {CircuitOpenUntilUtc}. CustomerId={CustomerId}, Platform={Platform}, StoreId={StoreId}",
                    c.Id,
                    c.CircuitOpenUntil,
                    customerId,
                    c.Platform,
                    c.StoreId);
            }
            else if (c.LastSyncAttempt != null && c.LastSyncAttempt.Value.AddSeconds(c.SyncIntervalSeconds) > now)
            {
                _logger.LogDebug(
                    "Skipping platform connection {ConnectionId} because sync interval has not elapsed. CustomerId={CustomerId}, Platform={Platform}, StoreId={StoreId}, LastAttemptUtc={LastAttemptUtc}, IntervalSeconds={IntervalSeconds}",
                    c.Id,
                    customerId,
                    c.Platform,
                    c.StoreId,
                    c.LastSyncAttempt,
                    c.SyncIntervalSeconds);
            }
        }

        var connectionResults = new List<OrderSyncConnectionResult>(dueConnections.Count);

        var mockGenerationScope = await BeginMockGenerationScopeAsync(customerId, db, ct).ConfigureAwait(false);
        try
        {
            foreach (var connection in dueConnections)
            {
                var result = await SyncConnectionAsync(customerId, db, connection, OrderSyncPass.Current, ct).ConfigureAwait(false);
                connectionResults.Add(result);
            }
        }
        finally
        {
            mockGenerationScope?.Dispose();
        }

        swCustomer.Stop();
        return Summarize(customerId, connectionResults, swCustomer.ElapsedMilliseconds, "sync");
    }

    /// <summary>
    /// One backfill turn: for every active connection whose last attempt succeeded and whose order history is still
    /// behind (see <see cref="IsHistoryBehind"/>), fetches the oldest missing window and moves the checkpoint to its end.
    /// The last turn reaches now, so the checkpoint ends at or after the hot window the current pass fetched, in the
    /// same cycle. Connections that are current, have an open circuit, or whose last attempt failed are left alone.
    /// </summary>
    public async Task<OrderSyncCustomerResult> BackfillCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var swCustomer = Stopwatch.StartNew();
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        if (!await IsOrderSyncEnabledAsync(db, ct).ConfigureAwait(false))
            return new OrderSyncCustomerResult(customerId, 0, 0, 0, 0, 0, 0, 0) { WasSyncDisabled = true };

        var now = _time.GetUtcNow().UtcDateTime;
        var connections = await db.PlatformConnections
            .Where(c => c.IsActive && c.ConsecutiveFailures == 0)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var behind = connections
            .Where(c => c.CircuitOpenUntil == null || c.CircuitOpenUntil <= now)
            .Where(c => IsHistoryBehind(c, ClientFor(c)))
            .ToList();

        var connectionResults = new List<OrderSyncConnectionResult>(behind.Count);
        foreach (var connection in behind)
        {
            var result = await SyncConnectionAsync(customerId, db, connection, OrderSyncPass.Backfill, ct).ConfigureAwait(false);
            connectionResults.Add(result);
        }

        swCustomer.Stop();
        return Summarize(customerId, connectionResults, swCustomer.ElapsedMilliseconds, "backfill");
    }

    /// <summary>
    /// A targeted reconciliation of one connection, outside the polling cycle: fetches the window from
    /// <paramref name="sinceUtc"/> (less the checkpoint overlap, at most one fetch window back) to now with the connection's
    /// own credentials, and ingests every order through the shared path. It never moves the checkpoint, the last attempt
    /// or the failure counters, so the polling pass and its outage recovery are unaffected; a failure is thrown to the
    /// caller. Used by the Trendyol GO Meal webhook stage pilot to read back what an event announced.
    /// </summary>
    internal async Task<OrderReconcileResult> ReconcileConnectionAsync(Guid customerId, Guid connectionId, DateTime sinceUtc, CancellationToken ct)
    {
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId, ct)
            .ConfigureAwait(false);
        if (connection is not { IsActive: true })
            throw new InvalidOperationException("The platform connection is missing or inactive.");

        var nowUtc = _time.GetUtcNow().UtcDateTime;
        if (connection.CircuitOpenUntil is { } openUntil && openUntil > nowUtc)
            throw new InvalidOperationException("The platform connection's circuit is open.");

        var client = ClientFor(connection) ?? throw new InvalidOperationException($"No IFoodPlatformClient registered for platform '{connection.Platform}'.");
        if (client.MaxFetchWindow is not { } maxWindow)
            throw new InvalidOperationException("Targeted reconciliation needs a client that fetches by window.");

        var start = OrderFetchWindowPlanner.AsUtc(sinceUtc) - OrderFetchWindowPlanner.CheckpointOverlap;
        if (start < nowUtc - maxWindow)
            start = nowUtc - maxWindow;
        if (start > nowUtc - OrderFetchWindowPlanner.CheckpointOverlap)
            start = nowUtc - OrderFetchWindowPlanner.CheckpointOverlap;
        var window = new OrderFetchWindow(start, nowUtc);

        var orders = await _windowedFetchPipeline.ExecuteAsync(
                async token => await client.FetchOrdersAsync(connection, window, token).ConfigureAwait(false),
                ct)
            .ConfigureAwait(false);

        // The window ends now and, for an event processed promptly, is short: its changes are dated like normal polling's.
        var milestonesObservedLive = window.EndUtc - window.StartUtc <= MilestoneObservationLimit;
        int inserted = 0, updated = 0, unchanged = 0, skipped = 0;
        foreach (var external in orders)
        {
            var r = await _ingestion.IngestPackageAsync(customerId, db, connection, external, milestonesObservedLive, ct).ConfigureAwait(false);
            if (r.Inserted) inserted++;
            if (r.Updated) updated++;
            if (r.Unchanged) unchanged++;
            if (r.Skipped) skipped++;
        }

        _logger.LogInformation(
            "Targeted reconciliation completed. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, WindowStartUtc={WindowStartUtc:O}, WindowEndUtc={WindowEndUtc:O}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Unchanged={UnchangedCount}, Skipped={SkippedCount}",
            customerId,
            connection.Id,
            connection.Platform,
            window.StartUtc,
            window.EndUtc,
            orders.Count,
            inserted,
            updated,
            unchanged,
            skipped);

        return new OrderReconcileResult(orders.Count, inserted, updated, unchanged, skipped);
    }

    /// <summary>
    /// Applies a status-only observation for one connection of the tenant (see
    /// <see cref="OrderIngestion.ApplyStatusObservationAsync"/>): never creates an order or changes its contents.
    /// </summary>
    internal async Task<OrderUpsertResult> ApplyStatusObservationAsync(
        Guid customerId,
        Guid connectionId,
        OrderStatusObservation observation,
        bool milestonesObservedLive,
        CancellationToken ct)
    {
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId, ct)
            .ConfigureAwait(false);
        if (connection is not { IsActive: true })
            throw new InvalidOperationException("The platform connection is missing or inactive.");

        return await _ingestion.ApplyStatusObservationAsync(customerId, db, connection, observation, milestonesObservedLive, ct)
            .ConfigureAwait(false);
    }

    /// <summary>The id of the tenant's order for a provider order id, or null when Wasla has not stored it.</summary>
    internal async Task<Guid?> FindOrderIdAsync(Guid customerId, FoodPlatform platform, string externalOrderId, CancellationToken ct)
    {
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        var key = OrderIngestion.IdempotencyKey(platform, externalOrderId);
        return await db.Orders.AsNoTracking()
            .Where(o => o.IdempotencyKey == key)
            .Select(o => (Guid?)o.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Whether the tenant's Order Sync setting allows fetching orders; the same gate the polling pass uses.</summary>
    internal async Task<bool> IsOrderSyncEnabledAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        return await IsOrderSyncEnabledAsync(db, ct).ConfigureAwait(false);
    }

    private IFoodPlatformClient? ClientFor(PlatformConnection connection) =>
        _platformClients.FirstOrDefault(c => c.Platform == connection.Platform);

    /// <summary>
    /// A windowed connection's history is behind while its checkpoint is older than its last current-pass attempt.
    /// A current pass that covers everything since the checkpoint moves it to that attempt's instant (the window ends
    /// at the same <c>now</c> stored as <c>LastSyncAttempt</c>); only a hot window fetched ahead of a gap leaves it
    /// older. Backfill keeps going until it has caught up, so it never stops just short of the hot window.
    /// </summary>
    private static bool IsHistoryBehind(PlatformConnection connection, IFoodPlatformClient? client) =>
        client?.MaxFetchWindow is not null
        && connection.LastSuccessfulSync is { } checkpoint
        && connection.LastSyncAttempt is { } attempt
        && OrderFetchWindowPlanner.AsUtc(checkpoint) < OrderFetchWindowPlanner.AsUtc(attempt);

    private OrderSyncCustomerResult Summarize(
        Guid customerId,
        IReadOnlyList<OrderSyncConnectionResult> connectionResults,
        long elapsedMs,
        string pass)
    {
        var result = new OrderSyncCustomerResult(
            customerId,
            connectionResults.Count,
            connectionResults.Sum(r => r.FetchedCount),
            connectionResults.Sum(r => r.InsertedCount),
            connectionResults.Sum(r => r.UpdatedCount),
            connectionResults.Sum(r => r.SkippedCount),
            connectionResults.Sum(r => r.UnchangedCount),
            connectionResults.Count(r => r.IsFailed))
        {
            Connections = connectionResults
                .Select(r => new OrderSyncConnectionSummary(
                    r.Platform.ToString(),
                    r.StoreId,
                    r.FetchedCount,
                    r.InsertedCount,
                    r.UpdatedCount,
                    r.SkippedCount,
                    r.UnchangedCount,
                    r.IsFailed,
                    r.ElapsedMs))
                .ToList(),
            BackfillPending = connectionResults.Any(r => r.BackfillPending)
        };

        _logger.LogDebug(
            "Completed {Pass} for customer {CustomerId}. Connections={ConnectionCount}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, FailedConnections={FailedConnections}, BackfillPending={BackfillPending}, ElapsedMs={ElapsedMs}",
            pass,
            customerId,
            result.ConnectionCount,
            result.FetchedCount,
            result.InsertedCount,
            result.UpdatedCount,
            result.SkippedCount,
            result.UnchangedCount,
            result.FailedConnections,
            result.BackfillPending,
            elapsedMs);

        return result;
    }

    private static readonly Guid TenantOperationalSettingsSingletonId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static async Task<bool> IsOrderSyncEnabledAsync(TenantDbContext db, CancellationToken ct)
    {
        // When no settings row exists (e.g. older tenant DB), preserve existing behavior: sync enabled.
        var row = await db.TenantOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TenantOperationalSettingsSingletonId, ct)
            .ConfigureAwait(false);

        return row?.OrderSyncEnabled ?? true;
    }

    private async Task<IDisposable?> BeginMockGenerationScopeAsync(
        Guid customerId,
        TenantDbContext db,
        CancellationToken ct)
    {
        if (_businessSubtypeReader is null)
            return null;

        var codes = await _businessSubtypeReader.GetSubtypeCodesAsync(customerId, ct).ConfigureAwait(false);
        if (codes is null)
            return null;

        var culture = await ReadReceiptLanguageAsync(db, ct).ConfigureAwait(false);
        return MockOrderGenerationContext.Begin(codes, culture);
    }

    private static async Task<string> ReadReceiptLanguageAsync(TenantDbContext db, CancellationToken ct)
    {
        var json = await db.TenantOperationalSettings.AsNoTracking()
            .Where(x => x.Id == TenantOperationalSettingsSingletonId)
            .Select(x => x.ReceiptTemplateSettingsJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
            return ReceiptLanguageCodes.Turkish;

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("ReceiptLanguage", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return ReceiptLanguageCodes.Normalize(property.Value.GetString());
                }
            }
        }
        catch (JsonException)
        {
            return ReceiptLanguageCodes.Turkish;
        }

        return ReceiptLanguageCodes.Turkish;
    }

    private async Task<OrderSyncConnectionResult> SyncConnectionAsync(
        Guid customerId,
        TenantDbContext db,
        PlatformConnection connection,
        OrderSyncPass pass,
        CancellationToken ct)
    {
        var swConn = Stopwatch.StartNew();
        // The current pass plans its window to end at exactly the instant it stores as LastSyncAttempt.
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        if (pass == OrderSyncPass.Current)
        {
            // Saved before any provider call, so no unsaved tenant change is held while the request limiter waits.
            connection.LastSyncAttempt = nowUtc;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A connection has no concurrency token, so the update found no row: it was removed after this pass
                // loaded it. Nothing was fetched; the pass goes on with the tenant's other connections.
                return RemovedDuringSync(customerId, db, connection, syncLog: null, swConn);
            }
        }

        var syncLog = new SyncLog
        {
            PlatformConnectionId = connection.Id,
            StartedAt = DateTime.UtcNow,
            Status = SyncStatus.Running
        };
        OrderFetchWindow? failedWindow = null;

        try
        {
            var client = _platformClients.FirstOrDefault(c => c.Platform == connection.Platform);
            if (client is null)
            {
                throw new InvalidOperationException($"No IFoodPlatformClient registered for platform '{connection.Platform}'.");
            }

            _logger.LogDebug(
                "Starting platform sync. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, IsActive={IsActive}, SyncIntervalSeconds={SyncIntervalSeconds}, LastSuccessfulSyncAt={LastSuccessfulSyncAtUtc}, LastSyncAttemptAt={LastSyncAttemptAtUtc}, CircuitOpenUntil={CircuitOpenUntilUtc}",
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                connection.IsActive,
                connection.SyncIntervalSeconds,
                connection.LastSuccessfulSync,
                connection.LastSyncAttempt,
                connection.CircuitOpenUntil);

            // Trendyol supplier id resolution (MVP):
            // If SupplierId is not explicitly set, StoreId is used as the supplier id.
            if (connection.Platform == FoodPlatform.TrendyolYemek)
            {
                if (string.IsNullOrWhiteSpace(connection.SupplierId) && !string.IsNullOrWhiteSpace(connection.StoreId))
                {
                    _logger.LogDebug(
                        "Resolved Trendyol supplier id from StoreId for connection {ConnectionId}. CustomerId={CustomerId}",
                        connection.Id,
                        customerId);
                }

                if (string.IsNullOrWhiteSpace(connection.SupplierId) && string.IsNullOrWhiteSpace(connection.StoreId))
                {
                    _logger.LogWarning(
                        "Skipping TrendyolYemek connection {ConnectionId} because supplier id could not be resolved. StoreId is empty and no supplierId setting exists. CustomerId={CustomerId}",
                        connection.Id,
                        customerId);

                    syncLog.Status = SyncStatus.Failed;
                    syncLog.FinishedAt = DateTime.UtcNow;
                    syncLog.ErrorMessage = "Supplier id could not be resolved (StoreId empty, SupplierId empty)";
                    db.SyncLogs.Add(syncLog);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    swConn.Stop();
                    return new OrderSyncConnectionResult(customerId, connection.Id, connection.Platform, connection.StoreId, 0, 0, 0, 0, 0, IsFailed: true)
                        { ElapsedMs = swConn.ElapsedMilliseconds };
                }
            }

            // LastSuccessfulSync is the checkpoint: everything the provider reports as modified before it has been
            // fetched and persisted. It moves to a window's end only after every page of that window is fetched and
            // every order in it is upserted, and only for a window that starts at the checkpoint, so it never skips
            // data. A hot window fetched ahead of a history gap leaves it where it is.
            IReadOnlyList<OrderFetchWindow> windows;
            bool advancesCheckpoint;
            if (pass == OrderSyncPass.Current)
            {
                var current = OrderFetchWindowPlanner.PlanCurrent(connection.LastSuccessfulSync, nowUtc, client.MaxFetchWindow);
                windows = [current.Window];
                advancesCheckpoint = current.AdvancesCheckpoint;
                if (!advancesCheckpoint)
                {
                    _logger.LogInformation(
                        "Order history is behind; fetching the current window first. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, CheckpointUtc={CheckpointUtc:O}, CurrentWindowStartUtc={CurrentWindowStartUtc:O}, CurrentWindowEndUtc={CurrentWindowEndUtc:O}",
                        customerId,
                        connection.Id,
                        connection.Platform,
                        connection.LastSuccessfulSync,
                        current.Window.StartUtc,
                        current.Window.EndUtc);
                }
            }
            else
            {
                windows = OrderFetchWindowPlanner.PlanBackfill(
                    connection.LastSuccessfulSync!.Value,
                    nowUtc,
                    client.MaxFetchWindow!.Value,
                    BackfillWindowsPerTurn);
                advancesCheckpoint = true;
            }

            var fetchPipeline = client.MaxFetchWindow is null ? _fetchPipeline : _windowedFetchPipeline;
            var connFetched = 0;
            var connInserted = 0;
            var connUpdated = 0;
            var connSkipped = 0;
            var connUnchanged = 0;
            var swUpsert = new Stopwatch();

            foreach (var window in windows)
            {
                ct.ThrowIfCancellationRequested();
                failedWindow = window;

                // The whole window is fetched (and throttled) before anything from it is applied to the tenant database.
                var swFetch = Stopwatch.StartNew();
                var externalOrders = await fetchPipeline.ExecuteAsync(
                        async token => await client.FetchOrdersAsync(connection, window, token).ConfigureAwait(false),
                        ct)
                    .ConfigureAwait(false);
                swFetch.Stop();

                connFetched += externalOrders.Count;
                syncLog.OrdersFetched = connFetched;

                _logger.LogDebug(
                    "Provider returned {OrderCount} orders. Pass={Pass}, Platform={Platform}, StoreId={StoreId}, ConnectionId={ConnectionId}, CustomerId={CustomerId}, WindowStartUtc={WindowStartUtc:O}, WindowEndUtc={WindowEndUtc:O}, FetchElapsedMs={FetchElapsedMs}, SampleExternalOrderIds={SampleExternalOrderIds}",
                    externalOrders.Count,
                    pass,
                    connection.Platform,
                    connection.StoreId,
                    connection.Id,
                    customerId,
                    window.StartUtc,
                    window.EndUtc,
                    swFetch.ElapsedMilliseconds,
                    externalOrders.Select(x => x.ExternalOrderId).Where(x => !string.IsNullOrWhiteSpace(x)).Take(10).ToArray());

                swUpsert.Start();
                // A status change found in this window happened inside it: everything the provider modified before the
                // window was already fetched by the windows that moved the checkpoint here. Only a current window that
                // starts at the checkpoint and is short enough dates such a change by when Wasla saw it; otherwise the
                // change may be hours old and its milestone time stays unknown (see MilestoneObservationLimit).
                var milestonesObservedLive = pass == OrderSyncPass.Current
                    && advancesCheckpoint
                    && window.EndUtc - window.StartUtc <= MilestoneObservationLimit;

                foreach (var external in externalOrders)
                {
                    var r = await _ingestion.IngestPackageAsync(customerId, db, connection, external, milestonesObservedLive, ct).ConfigureAwait(false);

                    if (r.Inserted) { syncLog.OrdersInserted++; connInserted++; }
                    if (r.Updated) { syncLog.OrdersUpdated++; connUpdated++; }
                    if (r.Skipped) { connSkipped++; }
                    if (r.Unchanged) { connUnchanged++; }
                }
                swUpsert.Stop();

                if (advancesCheckpoint)
                {
                    // Saved at once: a completed window is never re-fetched because a later wait or failure lost it.
                    connection.LastSuccessfulSync = window.EndUtc;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            failedWindow = null;
            connection.ConsecutiveFailures = 0;
            connection.CircuitOpenUntil = null;

            var backfillPending = IsHistoryBehind(connection, client);
            if (pass == OrderSyncPass.Backfill && !backfillPending)
            {
                _logger.LogInformation(
                    "Order history recovered. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, CheckpointUtc={CheckpointUtc:O}",
                    customerId,
                    connection.Id,
                    connection.Platform,
                    connection.LastSuccessfulSync);
            }

            syncLog.Status = SyncStatus.Success;
            syncLog.FinishedAt = DateTime.UtcNow;

            db.SyncLogs.Add(syncLog);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            swConn.Stop();
            _logger.LogDebug(
                "Completed platform sync. Pass={Pass}, CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, UpsertElapsedMs={UpsertElapsedMs}, ElapsedMs={ElapsedMs}",
                pass,
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                connFetched,
                connInserted,
                connUpdated,
                connSkipped,
                connUnchanged,
                swUpsert.ElapsedMilliseconds,
                swConn.ElapsedMilliseconds);

            return new OrderSyncConnectionResult(
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                connFetched,
                connInserted,
                connUpdated,
                connSkipped,
                connUnchanged,
                IsFailed: false)
            { ElapsedMs = swConn.ElapsedMilliseconds, BackfillPending = backfillPending };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed save leaves its changes tracked (an order graph still Added, an update's scalars and new items
            // after its transaction rolled back, an unsaved checkpoint). Drop them before anything else is saved, so
            // neither the failure report nor a later connection of this pass can write them.
            DiscardUnsavedChanges(db);

            syncLog.Status = SyncStatus.Failed;
            syncLog.FinishedAt = DateTime.UtcNow;
            syncLog.ErrorMessage = SanitizeErrorMessage(ex);

            if (!await RecordFailureAsync(customerId, db, connection, syncLog, ex, ct).ConfigureAwait(false))
                return RemovedDuringSync(customerId, db, connection, syncLog, swConn);

            swConn.Stop();
            _logger.LogError(
                ex,
                "Platform connection sync failed. Pass={Pass}, CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, WindowStartUtc={WindowStartUtc:O}, WindowEndUtc={WindowEndUtc:O}, CheckpointUtc={CheckpointUtc:O}, ElapsedMs={ElapsedMs}",
                pass,
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                failedWindow?.StartUtc,
                failedWindow?.EndUtc,
                connection.LastSuccessfulSync,
                swConn.ElapsedMilliseconds);

            return new OrderSyncConnectionResult(customerId, connection.Id, connection.Platform, connection.StoreId, syncLog.OrdersFetched, syncLog.OrdersInserted, syncLog.OrdersUpdated, 0, 0, IsFailed: true)
                { ElapsedMs = swConn.ElapsedMilliseconds };
        }
    }

    /// <summary>
    /// Forgets what a failed attempt left in the change tracker. Every completed step of a sync is saved at once, so
    /// nothing pending belongs to a completed step: order, item and option entities are detached (parent and children
    /// together, so a detached item cannot be found again through a tracked order), and a connection goes back to its
    /// stored values.
    /// </summary>
    private static void DiscardUnsavedChanges(TenantDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is PlatformConnection)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
            else
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// The operator removed the connection while this pass was syncing it (an update of its row found none). Orders the
    /// pass already stored stay; no failure is recorded for a connection that no longer exists, and the pass stops
    /// tracking it so no later save of the pass touches it.
    /// </summary>
    private OrderSyncConnectionResult RemovedDuringSync(
        Guid customerId,
        TenantDbContext db,
        PlatformConnection connection,
        SyncLog? syncLog,
        Stopwatch swConn)
    {
        db.Entry(connection).State = EntityState.Detached;
        swConn.Stop();
        _logger.LogInformation(
            "Platform connection was removed during sync; nothing more is recorded for it. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}",
            customerId,
            connection.Id,
            connection.Platform);

        return new OrderSyncConnectionResult(
            customerId,
            connection.Id,
            connection.Platform,
            connection.StoreId,
            syncLog?.OrdersFetched ?? 0,
            syncLog?.OrdersInserted ?? 0,
            syncLog?.OrdersUpdated ?? 0,
            0,
            0,
            IsFailed: false)
        { ElapsedMs = swConn.ElapsedMilliseconds };
    }

    /// <summary>
    /// Saves the failure report through a new context of the same tenant, so it can write only the
    /// <see cref="SyncLog"/>, the <see cref="IntegrationError"/> and the connection's failure counters. False, with
    /// nothing written, when the connection was removed meanwhile.
    /// </summary>
    private async Task<bool> RecordFailureAsync(
        Guid customerId,
        TenantDbContext db,
        PlatformConnection connection,
        SyncLog syncLog,
        Exception ex,
        CancellationToken ct)
    {
        await using var report = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        var stored = await report.PlatformConnections
            .SingleOrDefaultAsync(c => c.Id == connection.Id, ct)
            .ConfigureAwait(false);
        if (stored is null)
            return false;

        stored.ConsecutiveFailures++;
        if (stored.ConsecutiveFailures >= 5)
        {
            stored.CircuitOpenUntil = _time.GetUtcNow().UtcDateTime.AddMinutes(5);
        }

        report.SyncLogs.Add(syncLog);
        report.IntegrationErrors.Add(new IntegrationError
        {
            Platform = connection.Platform,
            PlatformConnectionId = connection.Id,
            ErrorType = ex.GetType().Name,
            ErrorMessage = syncLog.ErrorMessage ?? "Unknown error",
            IsResolved = false
        });

        await report.SaveChangesAsync(ct).ConfigureAwait(false);

        // The pass keeps tracking this connection: make its copy match what was stored, with nothing pending.
        var entry = db.Entry(connection);
        entry.CurrentValues.SetValues(stored);
        entry.OriginalValues.SetValues(stored);
        entry.State = EntityState.Unchanged;
        return true;
    }

    /// <summary>
    /// Longest current fetch window whose status changes are dated by when Wasla saw them (<c>AcceptedAt</c>,
    /// <c>DeliveredAt</c>, <c>CancelledAt</c>). Normal polling reads from five minutes before the checkpoint
    /// (<see cref="OrderFetchWindowPlanner.CheckpointOverlap"/>) to now, so its changes are minutes old at most. A longer
    /// window is recovery after a gap (or a first sync, which looks back an hour): a change in it can be anywhere in the
    /// window, and no provider field dates it (Trendyol GO <c>packageModificationDate</c> is the latest modification of
    /// any kind), so the milestone stays null rather than claiming the recovery time.
    /// </summary>
    internal static readonly TimeSpan MilestoneObservationLimit = TimeSpan.FromMinutes(15);

    private static readonly Regex CredentialFragment = new(
        @"(?i)\b(password|pwd|secret|token)\s*=\s*[^;,\s]+",
        RegexOptions.Compiled);

    internal static string SanitizeErrorMessage(Exception ex)
    {
        var msg = ex.Message ?? "Unknown error";
        msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();

        var bodyIndex = msg.IndexOf("Body:", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex >= 0)
            msg = msg[..bodyIndex].Trim().TrimEnd('.', ':', '-', ' ');

        msg = CredentialFragment.Replace(msg, "$1=[redacted]");
        if (string.IsNullOrWhiteSpace(msg))
            msg = ex.GetType().Name;

        return msg.Length <= 500 ? msg : msg[..500];
    }
}

/// <summary>Which part of a connection's work a sync call does.</summary>
internal enum OrderSyncPass
{
    /// <summary>The window ending now (all of the gap when it fits one window, otherwise the hot window).</summary>
    Current,

    /// <summary>The oldest missing window after a history gap.</summary>
    Backfill
}

/// <summary>What a targeted reconciliation fetched and did.</summary>
internal sealed record OrderReconcileResult(int FetchedCount, int InsertedCount, int UpdatedCount, int UnchangedCount, int SkippedCount);
