using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private readonly IOrderStatusMapper _statusMapper;
    private readonly IOrderAutoApproveService _autoApprove;
    private readonly IOrderReceiptCreationService _receiptCreation;
    private readonly ILogger<OrderSyncService> _logger;
    private readonly ITenantBusinessSubtypeReader? _businessSubtypeReader;
    private readonly TimeProvider _time;
    private readonly ITenantChangeNotifier _notifier;

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
        _statusMapper = statusMapper;
        _autoApprove = autoApprove;
        _receiptCreation = receiptCreation;
        _logger = logger;
        _businessSubtypeReader = businessSubtypeReader;
        _time = time ?? TimeProvider.System;
        _notifier = notifier;
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
                    var r = await UpsertOrderAsync(customerId, db, external, milestonesObservedLive, ct).ConfigureAwait(false);

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

    private async Task<OrderUpsertResult> UpsertOrderAsync(
      Guid customerId,
      TenantDbContext db,
      ExternalOrderDto external,
      bool milestonesObservedLive,
      CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(external.ExternalOrderId))
        {
            _logger.LogWarning(
                "Skipping external order because ExternalOrderId is missing. Platform={Platform}, ExternalOrderCode={ExternalOrderCode}",
                external.Platform,
                external.ExternalOrderCode);
            return new OrderUpsertResult(false, false, true, false, null);
        }

        var input = $"{external.Platform}:{external.ExternalOrderId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var key = Convert.ToBase64String(hash);

        var nowUtc = DateTime.UtcNow;
        external = WithPlausibleProviderTimes(external, nowUtc);
        var newStatus = _statusMapper.MapToInternalStatus(
            external.Platform,
            external.ExternalStatus);

        var existing = await db.Orders
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var order = MapToOrderEntity(
                external,
                key,
                receivedAtUtc: nowUtc,
                internalStatus: newStatus,
                nowUtc: nowUtc,
                milestonesObservedLive: milestonesObservedLive);

            order.ChangeVersion = OrderChangeVersions.Initial;
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // The new order is committed: open Live Screens read their snapshot again.
            await TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(order.Id, order.ChangeVersion), _logger)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Inserted order. OrderId={OrderId}, ExternalOrderId={ExternalOrderId}, Platform={Platform}, Status={Status}, ReceivedAtUtc={ReceivedAtUtc}",
                order.Id,
                order.ExternalOrderId,
                order.Platform,
                order.InternalStatus,
                order.ReceivedAt);

            try
            {
                await _autoApprove.ProcessNewlyInsertedOrderAsync(
                    customerId,
                    order.Id,
                    order.InternalStatus,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Auto-approve processing failed but sync continued. CustomerId={CustomerId}, OrderId={OrderId}",
                    customerId,
                    order.Id);
            }

            await TryCreateReceiptIfProviderAcceptedAsync(
                customerId,
                order.Id,
                oldStatus: null,
                newStatus: order.InternalStatus,
                ct).ConfigureAwait(false);

            return new OrderUpsertResult(true, false, false, false, order.ExternalOrderId);
        }

        // Read child rows before any transaction. An unchanged order must not open the
        // mutation transaction, delete children, or call SaveChanges for the order.
        var persistedItems = await LoadPersistedItemSnapshotsAsync(db, existing.Id, ct).ConfigureAwait(false);
        if (IsSemanticallyUnchanged(existing, persistedItems, external, newStatus))
            return new OrderUpsertResult(false, false, false, true, existing.ExternalOrderId);

        await using var tx = await db.Database.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        // An operator action or another Worker may have committed a status or items since the reads above (WAS-123).
        // Lock the order row first with the UpdatedAt write this update makes anyway, then read the row and its items
        // again: the comparison and the merge below start from what is committed, and nothing can change it before this
        // transaction commits. The lock write never moves UpdatedAt back past a writer that committed during the wait.
        await db.Orders
            .Where(o => o.Id == existing.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, o => o.UpdatedAt > nowUtc ? o.UpdatedAt : nowUtc), ct)
            .ConfigureAwait(false);
        await db.Entry(existing).ReloadAsync(ct).ConfigureAwait(false);
        persistedItems = await LoadPersistedItemSnapshotsAsync(db, existing.Id, ct).ConfigureAwait(false);

        // The change is applied now, after any wait for the lock, and never before the order's last committed change.
        nowUtc = DateTime.UtcNow;
        if (nowUtc < existing.UpdatedAt)
            nowUtc = existing.UpdatedAt;

        if (IsSemanticallyUnchanged(existing, persistedItems, external, newStatus))
        {
            // The committed row already holds what the provider reports: write nothing, as for an unchanged order.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            db.Entry(existing).State = EntityState.Detached;
            return new OrderUpsertResult(false, false, false, true, existing.ExternalOrderId);
        }

        var oldStatus = existing.InternalStatus;
        var oldTotal = existing.TotalAmount;
        var oldPlatformStatus = existing.PlatformStatus;

        // EN: Provider status can lag behind local operator actions; merge instead of blindly overwriting InternalStatus so stale New/Accepted never undoes manual progress.
        // TR: Provider statüsü paneldeki manuel aksiyonların gerisinde kalabilir; InternalStatus'u körlemesine ezmemek için merge ediyoruz; eski New/Accepted manuel ilerlemeyi geri almasın.
        var mergedStatus = MergeInternalStatusForSync(existing.InternalStatus, newStatus);

        existing.InternalStatus = mergedStatus;
        existing.PlatformStatus = external.ExternalStatus;
        existing.CustomerName = external.CustomerName;
        existing.TotalAmount = external.Total;
        existing.DeliveryFee = external.DeliveryFee;
        existing.ServiceFee = external.ServiceFee;
        existing.PaymentMethod = external.PaymentMethod;
        existing.PaymentStatus = external.PaymentStatus;
        existing.RawPayloadJson = external.RawPayloadJson;
        // ReceivedAt (first import) is never written here. A provider time the payload lacks keeps the stored one.
        existing.CreatedAtPlatform = external.OrderedAtUtc ?? existing.CreatedAtPlatform;
        existing.PlatformModifiedAt = external.ModifiedAtUtc ?? existing.PlatformModifiedAt;
        existing.ExternalOrderCode = external.ExternalOrderCode;
        existing.CustomerPhone = external.CustomerPhone;
        existing.CustomerAddress = external.CustomerAddress;
        existing.CustomerNote = NormalizeCustomerNote(external.CustomerNote);
        existing.UpdatedAt = nowUtc;

        ApplyStatusTransitionTimestamps(
            existing,
            oldStatus,
            mergedStatus,
            nowUtc,
            milestonesObservedLive);

        await db.OrderItemOptions
            .Where(o => db.OrderItems
                .Where(i => i.OrderId == existing.Id)
                .Select(i => i.Id)
                .Contains(o.OrderItemId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        await db.OrderItems
            .Where(i => i.OrderId == existing.Id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Items and options keep the provider's list order (Sequence), as on insert.
        var itemSequence = 0;
        foreach (var itemDto in external.Items)
        {
            var item = new OrderItem
            {
                OrderId = existing.Id,
                ProductName = itemDto.ProductName,
                Quantity = itemDto.Quantity,
                UnitPrice = itemDto.UnitPrice,
                TotalPrice = itemDto.TotalPrice,
                Notes = itemDto.Notes,
                Sequence = itemSequence++,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };

            var optionSequence = 0;
            foreach (var opt in itemDto.Options)
            {
                item.Options.Add(new OrderItemOption
                {
                    Name = opt.Name,
                    Price = opt.Price,
                    Sequence = optionSequence++,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                });
            }

            db.OrderItems.Add(item);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var changeVersion = await OrderChangeVersions.NextAsync(db, existing.Id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        await TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(existing.Id, changeVersion), _logger)
            .ConfigureAwait(false);

        await TryCreateReceiptIfProviderAcceptedAsync(
            customerId,
            existing.Id,
            oldStatus,
            mergedStatus,
            ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Updated order {ExternalOrderId}. Status {OldStatus} -> {MergedStatus} (external mapped {ExternalMappedStatus}), Total {OldTotal} -> {NewTotal}, PlatformStatus {OldPlatformStatus} -> {NewPlatformStatus}",
            existing.ExternalOrderId,
            oldStatus,
            mergedStatus,
            newStatus,
            oldTotal,
            existing.TotalAmount,
            oldPlatformStatus,
            existing.PlatformStatus);

        return new OrderUpsertResult(false, true, false, false, existing.ExternalOrderId);
    }

    private async Task TryCreateReceiptIfProviderAcceptedAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus? oldStatus,
        OrderStatus newStatus,
        CancellationToken ct)
    {
        if (!ShouldCreateReceiptForAcceptedProviderStatus(oldStatus, newStatus))
            return;

        try
        {
            await _receiptCreation.TryCreateOnOrderAcceptedAsync(customerId, orderId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Receipt creation after provider-accepted order failed but sync continued. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
        }
    }

    private static string? NormalizeCustomerNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        const int maxLength = 2000;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool ShouldCreateReceiptForAcceptedProviderStatus(OrderStatus? oldStatus, OrderStatus newStatus)
    {
        if (newStatus != OrderStatus.Accepted)
            return false;

        return oldStatus is null or not OrderStatus.Accepted;
    }

    /// <summary>Earliest provider time accepted as real; anything before it is a missing value read as zero.</summary>
    internal static readonly DateTime EarliestProviderTimeUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How far a provider time may lie after Wasla's clock (clock skew) and still be accepted.</summary>
    internal static readonly TimeSpan ProviderClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A provider order time as UTC when it is plausible, otherwise null (WAS-125): before
    /// <see cref="EarliestProviderTimeUtc"/>, or more than <see cref="ProviderClockTolerance"/> after
    /// <paramref name="nowUtc"/>. An unspecified kind is taken as UTC, which is what every client produces.
    /// </summary>
    internal static DateTime? PlausibleProviderTime(DateTime? value, DateTime nowUtc)
    {
        if (value is not { } time)
            return null;

        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return utc < EarliestProviderTimeUtc || utc > nowUtc + ProviderClockTolerance ? null : utc;
    }

    private ExternalOrderDto WithPlausibleProviderTimes(ExternalOrderDto external, DateTime nowUtc)
    {
        var orderedAt = PlausibleProviderTime(external.OrderedAtUtc, nowUtc);
        var modifiedAt = PlausibleProviderTime(external.ModifiedAtUtc, nowUtc);
        if (orderedAt is null && external.OrderedAtUtc is not null || modifiedAt is null && external.ModifiedAtUtc is not null)
        {
            _logger.LogWarning(
                "Provider order time ignored as implausible. Platform={Platform}, ExternalOrderId={ExternalOrderId}, CreationTimeIgnored={CreationTimeIgnored}, ModificationTimeIgnored={ModificationTimeIgnored}",
                external.Platform,
                external.ExternalOrderId,
                orderedAt is null && external.OrderedAtUtc is not null,
                modifiedAt is null && external.ModifiedAtUtc is not null);
        }

        return external with { OrderedAtUtc = orderedAt, ModifiedAtUtc = modifiedAt };
    }

    private static Order MapToOrderEntity(
        ExternalOrderDto external,
        string idempotencyKey,
        DateTime receivedAtUtc,
        OrderStatus internalStatus,
        DateTime nowUtc,
        bool milestonesObservedLive)
    {
        var order = new Order
        {
            Platform = external.Platform,
            ExternalOrderId = external.ExternalOrderId,
            IdempotencyKey = idempotencyKey,
            InternalStatus = internalStatus,
            PlatformStatus = external.ExternalStatus,
            CustomerName = external.CustomerName,
            TotalAmount = external.Total,
            DeliveryFee = external.DeliveryFee,
            ServiceFee = external.ServiceFee,
            PaymentMethod = external.PaymentMethod,
            PaymentStatus = external.PaymentStatus,
            CreatedAtPlatform = external.OrderedAtUtc,
            PlatformModifiedAt = external.ModifiedAtUtc,
            ReceivedAt = receivedAtUtc,
            RawPayloadJson = external.RawPayloadJson,
            ExternalOrderCode = external.ExternalOrderCode,
            CustomerPhone = external.CustomerPhone,
            CustomerAddress = external.CustomerAddress,
            CustomerNote = NormalizeCustomerNote(external.CustomerNote)
        };

        // An order first seen already Accepted, Delivered or Cancelled gets those times only from a live window.
        ApplyStatusTransitionTimestamps(order, OrderStatus.New, internalStatus, nowUtc, milestonesObservedLive);

        // Sequence keeps the order in which the provider listed the lines and their options; the mapped
        // ExternalOrderDto lists them in that order. Readers order by it (OrderItemOrdering).
        var itemSequence = 0;
        foreach (var itemDto in external.Items)
        {
            var item = new OrderItem
            {
                ProductName = itemDto.ProductName,
                Quantity = itemDto.Quantity,
                UnitPrice = itemDto.UnitPrice,
                TotalPrice = itemDto.TotalPrice,
                Notes = itemDto.Notes,
                Sequence = itemSequence++,
            };

            var optionSequence = 0;
            foreach (var opt in itemDto.Options)
            {
                item.Options.Add(new OrderItemOption
                {
                    Name = opt.Name,
                    Price = opt.Price,
                    Sequence = optionSequence++
                });
            }

            order.Items.Add(item);
        }

        return order;
    }

    // EN: Merge prevents downgrades when external/mock payloads lag behind OrderHub; terminals (Delivered/Cancelled/Failed) stay locked except allowed cancel/fail propagation.
    // TR: Dış/mock payload OrderHub'un gerisinde kaldığında düşürme olmasın diye merge; terminal durumlar (Delivered/Cancelled/Failed) kilitlenir, iptal/hata ise kurallara göre geçer.
    private static OrderStatus MergeInternalStatusForSync(OrderStatus existing, OrderStatus incomingFromExternal)
    {
        if (existing == OrderStatus.Delivered)
            return existing;

        if (existing == OrderStatus.Cancelled || existing == OrderStatus.Failed)
            return existing;

        if (incomingFromExternal == OrderStatus.Cancelled || incomingFromExternal == OrderStatus.Failed)
            return incomingFromExternal;

        var existingRank = OperationalProgressRank(existing);
        var incomingRank = OperationalProgressRank(incomingFromExternal);

        if (incomingRank < existingRank)
            return existing;

        return incomingFromExternal;
    }

    private static int OperationalProgressRank(OrderStatus status) => status switch
    {
        OrderStatus.New => 0,
        OrderStatus.Accepted => 1,
        OrderStatus.Preparing => 2,
        OrderStatus.ReadyForPickup => 3,
        OrderStatus.OnTheWay => 4,
        OrderStatus.Delivered => 5,
        OrderStatus.Cancelled => -1,
        OrderStatus.Failed => -1,
        _ => 0
    };

    /// <summary>
    /// Longest current fetch window whose status changes are dated by when Wasla saw them (<c>AcceptedAt</c>,
    /// <c>DeliveredAt</c>, <c>CancelledAt</c>). Normal polling reads from five minutes before the checkpoint
    /// (<see cref="OrderFetchWindowPlanner.CheckpointOverlap"/>) to now, so its changes are minutes old at most. A longer
    /// window is recovery after a gap (or a first sync, which looks back an hour): a change in it can be anywhere in the
    /// window, and no provider field dates it (Trendyol GO <c>packageModificationDate</c> is the latest modification of
    /// any kind), so the milestone stays null rather than claiming the recovery time.
    /// </summary>
    internal static readonly TimeSpan MilestoneObservationLimit = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sets the milestone a sync-observed status change reaches, once, to <paramref name="nowUtc"/> when the change was
    /// observed live (<see cref="MilestoneObservationLimit"/>); otherwise leaves it null: the time is unknown.
    /// </summary>
    private static void ApplyStatusTransitionTimestamps(
        Order order,
        OrderStatus oldStatus,
        OrderStatus newStatus,
        DateTime nowUtc,
        bool milestonesObservedLive)
    {
        if (oldStatus == newStatus || !milestonesObservedLive) return;

        switch (newStatus)
        {
            case OrderStatus.Accepted when order.AcceptedAt is null:
                order.AcceptedAt = nowUtc;
                break;
            case OrderStatus.Delivered when order.DeliveredAt is null:
                order.DeliveredAt = nowUtc;
                break;
            case OrderStatus.Cancelled when order.CancelledAt is null:
                order.CancelledAt = nowUtc;
                break;
        }
    }

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

    /// <summary>
    /// Compares the persisted order with the provider payload using the values Wasla
    /// actually stores. Equality is decided before any mutation.
    /// </summary>
    /// <remarks>
    /// RawPayloadJson is a diagnostic copy of the last provider body. Nothing in the
    /// application reads it after save, and provider JSON is not canonical: property
    /// order and non-domain metadata change between overlapping polls. A raw-payload-only
    /// difference does not rewrite the order. When any synchronized field changes, the
    /// existing update path still stores the latest payload.
    /// Subtotal and ExternalItemId are not persisted, so they are not compared.
    /// Money columns are decimal(18,2); values are compared at that scale.
    /// Item and modifier order is not stored, so collections are compared as multisets.
    /// InternalStatus is compared after <see cref="MergeInternalStatusForSync"/>, so a
    /// stale provider status cannot look like a change when the merge keeps operator state.
    /// </remarks>
    private static bool IsSemanticallyUnchanged(
        Order existing,
        IReadOnlyList<PersistedOrderItemSnapshot> persistedItems,
        ExternalOrderDto external,
        OrderStatus mappedStatus)
    {
        var mergedStatus = MergeInternalStatusForSync(existing.InternalStatus, mappedStatus);
        if (existing.InternalStatus != mergedStatus)
            return false;

        if (!SameStoredText(existing.PlatformStatus, external.ExternalStatus))
            return false;

        if (!SameStoredText(existing.ExternalOrderCode, external.ExternalOrderCode))
            return false;

        if (!SameStoredText(existing.CustomerName, external.CustomerName))
            return false;

        if (!SameStoredText(existing.CustomerPhone, external.CustomerPhone))
            return false;

        if (!SameStoredText(existing.CustomerAddress, external.CustomerAddress))
            return false;

        if (!string.Equals(
                NormalizeCustomerNote(existing.CustomerNote),
                NormalizeCustomerNote(external.CustomerNote),
                StringComparison.Ordinal))
            return false;

        if (!MoneyEquals(existing.TotalAmount, external.Total))
            return false;

        if (!MoneyEquals(existing.DeliveryFee, external.DeliveryFee))
            return false;

        if (!MoneyEquals(existing.ServiceFee, external.ServiceFee))
            return false;

        if (existing.PaymentMethod != external.PaymentMethod)
            return false;

        if (existing.PaymentStatus != external.PaymentStatus)
            return false;

        // A provider time the payload lacks keeps the stored one (see UpdateExisting), so only a sent value can differ.
        if (external.OrderedAtUtc is { } orderedAt && existing.CreatedAtPlatform != orderedAt)
            return false;

        if (external.ModifiedAtUtc is { } modifiedAt && existing.PlatformModifiedAt != modifiedAt)
            return false;

        return ItemsSemanticallyEqual(persistedItems, external.Items);
    }

    private static async Task<IReadOnlyList<PersistedOrderItemSnapshot>> LoadPersistedItemSnapshotsAsync(
        TenantDbContext db,
        Guid orderId,
        CancellationToken ct)
    {
        // In the order the screens show (OrderItemOrdering), so the comparison below sees the provider's order.
        var rows = await db.OrderItems
            .AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderBy(i => i.Sequence).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return [];

        var itemIds = rows.Select(r => r.Id).ToArray();
        var optionRows = await db.OrderItemOptions
            .AsNoTracking()
            .Where(o => itemIds.Contains(o.OrderItemId))
            .OrderBy(o => o.Sequence).ThenBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new
            {
                o.OrderItemId,
                o.Name,
                o.Price
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var optionsByItem = optionRows.ToLookup(o => o.OrderItemId);
        return rows
            .Select(r => new PersistedOrderItemSnapshot(
                r.ProductName,
                r.Quantity,
                r.UnitPrice,
                r.TotalPrice,
                r.Notes,
                optionsByItem[r.Id]
                    .Select(o => new PersistedOptionSnapshot(o.Name, o.Price))
                    .ToArray()))
            .ToList();
    }

    /// <summary>
    /// Item by item in order: the provider's order of the lines, and of each line's options, is part of the order
    /// (WAS-44, <c>Sequence</c>), so the same lines in a different order are a change that rewrites the positions.
    /// </summary>
    private static bool ItemsSemanticallyEqual(
        IReadOnlyList<PersistedOrderItemSnapshot> persistedItems,
        IReadOnlyCollection<ExternalOrderItemDto>? incomingItems)
    {
        var incoming = incomingItems ?? [];
        if (persistedItems.Count != incoming.Count)
            return false;

        var left = persistedItems.Select(ToComparableItem).ToList();
        var right = incoming.Select(ToComparableItem).ToList();

        for (var i = 0; i < left.Count; i++)
        {
            if (CompareItems(left[i], right[i]) != 0)
                return false;
        }

        return true;
    }

    private static ComparableItem ToComparableItem(PersistedOrderItemSnapshot item) =>
        new(
            item.ProductName ?? string.Empty,
            item.Quantity,
            NormalizeMoney(item.UnitPrice),
            NormalizeMoney(item.TotalPrice),
            NormalizeItemNote(item.Notes),
            NormalizeOptions(item.Options.Select(o => (o.Name, o.Price))));

    private static ComparableItem ToComparableItem(ExternalOrderItemDto item) =>
        new(
            item.ProductName ?? string.Empty,
            item.Quantity,
            NormalizeMoney(item.UnitPrice),
            NormalizeMoney(item.TotalPrice),
            NormalizeItemNote(item.Notes),
            NormalizeOptions((item.Options ?? []).Select(o => (o.Name, o.Price))));

    // Kept in the given order: the options' order is compared too.
    private static ComparableOption[] NormalizeOptions(IEnumerable<(string Name, decimal Price)> options) =>
        options
            .Select(o => new ComparableOption(o.Name ?? string.Empty, NormalizeMoney(o.Price)))
            .ToArray();

    private static int CompareItems(ComparableItem left, ComparableItem right)
    {
        var comparison = string.CompareOrdinal(left.ProductName, right.ProductName);
        if (comparison != 0)
            return comparison;

        comparison = left.Quantity.CompareTo(right.Quantity);
        if (comparison != 0)
            return comparison;

        comparison = left.UnitPrice.CompareTo(right.UnitPrice);
        if (comparison != 0)
            return comparison;

        comparison = left.TotalPrice.CompareTo(right.TotalPrice);
        if (comparison != 0)
            return comparison;

        comparison = string.CompareOrdinal(left.Notes, right.Notes);
        if (comparison != 0)
            return comparison;

        comparison = left.Options.Length.CompareTo(right.Options.Length);
        if (comparison != 0)
            return comparison;

        for (var i = 0; i < left.Options.Length; i++)
        {
            comparison = string.CompareOrdinal(left.Options[i].Name, right.Options[i].Name);
            if (comparison != 0)
                return comparison;

            comparison = left.Options[i].Price.CompareTo(right.Options[i].Price);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static bool SameStoredText(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool MoneyEquals(decimal left, decimal right) =>
        NormalizeMoney(left) == NormalizeMoney(right);

    private static decimal NormalizeMoney(decimal value) =>
        decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    private static string? NormalizeItemNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private const int MoneyScale = 2;

    private sealed record PersistedOrderItemSnapshot(
        string ProductName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string? Notes,
        PersistedOptionSnapshot[] Options);

    private readonly record struct PersistedOptionSnapshot(string Name, decimal Price);

    private readonly record struct ComparableOption(string Name, decimal Price);

    private sealed record ComparableItem(
        string ProductName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string? Notes,
        ComparableOption[] Options);
}

/// <summary>Which part of a connection's work a sync call does.</summary>
internal enum OrderSyncPass
{
    /// <summary>The window ending now (all of the gap when it fits one window, otherwise the hot window).</summary>
    Current,

    /// <summary>The oldest missing window after a history gap.</summary>
    Backfill
}
