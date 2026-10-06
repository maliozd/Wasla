using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mock;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Wasla.Infrastructure.Sync;

public sealed class OrderSyncService : IOrderSyncService
{
    private readonly ITenantDbContextFactory _customerDbFactory;
    private readonly IEnumerable<IFoodPlatformClient> _platformClients;
    private readonly IOrderStatusMapper _statusMapper;
    private readonly IOrderAutoApproveService _autoApprove;
    private readonly IOrderReceiptCreationService _receiptCreation;
    private readonly ILogger<OrderSyncService> _logger;
    private readonly ITenantBusinessSubtypeReader? _businessSubtypeReader;
    private readonly TimeProvider _time;

    private static readonly ResiliencePipeline<IReadOnlyCollection<ExternalOrderDto>> _fetchPipeline =
        new ResiliencePipelineBuilder<IReadOnlyCollection<ExternalOrderDto>>()
            .AddRetry(new RetryStrategyOptions<IReadOnlyCollection<ExternalOrderDto>>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<IReadOnlyCollection<ExternalOrderDto>>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
                    .Handle<TimeoutRejectedException>()
            })
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();

    public OrderSyncService(
        ITenantDbContextFactory customerDbFactory,
        IEnumerable<IFoodPlatformClient> platformClients,
        IOrderStatusMapper statusMapper,
        IOrderAutoApproveService autoApprove,
        IOrderReceiptCreationService receiptCreation,
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

        var now = DateTime.UtcNow;
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

        var fetched = 0;
        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        var unchanged = 0;
        var failedConnections = 0;
        var connectionResults = new List<OrderSyncConnectionResult>(dueConnections.Count);

        var mockGenerationScope = await BeginMockGenerationScopeAsync(customerId, db, ct).ConfigureAwait(false);
        try
        {
            foreach (var connection in dueConnections)
            {
                var result = await SyncConnectionAsync(customerId, db, connection, ct).ConfigureAwait(false);
                connectionResults.Add(result);
                fetched += result.FetchedCount;
                inserted += result.InsertedCount;
                updated += result.UpdatedCount;
                skipped += result.SkippedCount;
                unchanged += result.UnchangedCount;
                if (result.IsFailed) failedConnections++;
            }
        }
        finally
        {
            mockGenerationScope?.Dispose();
        }

        swCustomer.Stop();
        _logger.LogDebug(
            "Completed sync for customer {CustomerId}. Connections={ConnectionCount}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, FailedConnections={FailedConnections}, ElapsedMs={ElapsedMs}",
            customerId,
            dueConnections.Count,
            fetched,
            inserted,
            updated,
            skipped,
            unchanged,
            failedConnections,
            swCustomer.ElapsedMilliseconds);

        var summaries = connectionResults
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
            .ToList();

        return new OrderSyncCustomerResult(
            customerId,
            dueConnections.Count,
            fetched,
            inserted,
            updated,
            skipped,
            unchanged,
            failedConnections)
        { Connections = summaries };
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
        CancellationToken ct)
    {
        var swConn = Stopwatch.StartNew();
        connection.LastSyncAttempt = DateTime.UtcNow;

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
            // fetched and persisted. Windows run oldest first. The checkpoint moves to a window's end only after every
            // page of that window is fetched and every order in it is upserted, so a failure never skips data.
            var plan = OrderFetchWindowPlanner.Plan(
                connection.LastSuccessfulSync,
                _time.GetUtcNow().UtcDateTime,
                client.MaxFetchWindow);

            if (plan.Windows.Count > 1 || plan.HasMore)
            {
                _logger.LogInformation(
                    "Recovering platform orders after a sync gap. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, CheckpointUtc={CheckpointUtc:O}, Windows={WindowCount}, FromUtc={FromUtc:O}, ThroughUtc={ThroughUtc:O}, MoreRunsNeeded={MoreRunsNeeded}",
                    customerId,
                    connection.Id,
                    connection.Platform,
                    connection.LastSuccessfulSync,
                    plan.Windows.Count,
                    plan.Windows[0].StartUtc,
                    plan.Windows[^1].EndUtc,
                    plan.HasMore);
            }

            var connFetched = 0;
            var connInserted = 0;
            var connUpdated = 0;
            var connSkipped = 0;
            var connUnchanged = 0;
            var swUpsert = new Stopwatch();

            foreach (var window in plan.Windows)
            {
                ct.ThrowIfCancellationRequested();
                failedWindow = window;

                var swFetch = Stopwatch.StartNew();
                var externalOrders = await _fetchPipeline.ExecuteAsync(
                        async token => await client.FetchOrdersAsync(connection, window, token).ConfigureAwait(false),
                        ct)
                    .ConfigureAwait(false);
                swFetch.Stop();

                connFetched += externalOrders.Count;
                syncLog.OrdersFetched = connFetched;

                _logger.LogDebug(
                    "Provider returned {OrderCount} orders. Platform={Platform}, StoreId={StoreId}, ConnectionId={ConnectionId}, CustomerId={CustomerId}, WindowStartUtc={WindowStartUtc:O}, WindowEndUtc={WindowEndUtc:O}, FetchElapsedMs={FetchElapsedMs}, SampleExternalOrderIds={SampleExternalOrderIds}",
                    externalOrders.Count,
                    connection.Platform,
                    connection.StoreId,
                    connection.Id,
                    customerId,
                    window.StartUtc,
                    window.EndUtc,
                    swFetch.ElapsedMilliseconds,
                    externalOrders.Select(x => x.ExternalOrderId).Where(x => !string.IsNullOrWhiteSpace(x)).Take(10).ToArray());

                swUpsert.Start();
                foreach (var external in externalOrders)
                {
                    var r = await UpsertOrderAsync(customerId, db, external, ct).ConfigureAwait(false);

                    if (r.Inserted) { syncLog.OrdersInserted++; connInserted++; }
                    if (r.Updated) { syncLog.OrdersUpdated++; connUpdated++; }
                    if (r.Skipped) { connSkipped++; }
                    if (r.Unchanged) { connUnchanged++; }
                }
                swUpsert.Stop();

                connection.LastSuccessfulSync = window.EndUtc;
            }

            failedWindow = null;
            connection.ConsecutiveFailures = 0;
            connection.CircuitOpenUntil = null;

            syncLog.Status = SyncStatus.Success;
            syncLog.FinishedAt = DateTime.UtcNow;

            db.SyncLogs.Add(syncLog);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            swConn.Stop();
            _logger.LogDebug(
                "Completed platform sync. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, UpsertElapsedMs={UpsertElapsedMs}, ElapsedMs={ElapsedMs}",
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
            { ElapsedMs = swConn.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            connection.ConsecutiveFailures++;
            if (connection.ConsecutiveFailures >= 5)
            {
                connection.CircuitOpenUntil = DateTime.UtcNow.AddMinutes(5);
            }

            syncLog.Status = SyncStatus.Failed;
            syncLog.FinishedAt = DateTime.UtcNow;
            syncLog.ErrorMessage = SanitizeErrorMessage(ex);

            db.SyncLogs.Add(syncLog);

            db.IntegrationErrors.Add(new IntegrationError
            {
                Platform = connection.Platform,
                PlatformConnectionId = connection.Id,
                ErrorType = ex.GetType().Name,
                ErrorMessage = syncLog.ErrorMessage ?? "Unknown error",
                IsResolved = false
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            swConn.Stop();
            _logger.LogError(
                ex,
                "Platform connection sync failed. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, WindowStartUtc={WindowStartUtc:O}, WindowEndUtc={WindowEndUtc:O}, CheckpointUtc={CheckpointUtc:O}, ElapsedMs={ElapsedMs}",
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

    private async Task<OrderUpsertResult> UpsertOrderAsync(
      Guid customerId,
      TenantDbContext db,
      ExternalOrderDto external,
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
                nowUtc: nowUtc);

            db.Orders.Add(order);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

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

        var oldStatus = existing.InternalStatus;
        var oldTotal = existing.TotalAmount;
        var oldPlatformStatus = existing.PlatformStatus;

        // EN: Provider status can lag behind local operator actions; merge instead of blindly overwriting InternalStatus so stale New/Accepted never undoes manual progress.
        // TR: Provider statüsü paneldeki manuel aksiyonların gerisinde kalabilir; InternalStatus'u körlemesine ezmemek için merge ediyoruz; eski New/Accepted manuel ilerlemeyi geri almasın.
        var mergedStatus = MergeInternalStatusForSync(existing.InternalStatus, newStatus);

        await using var tx = await db.Database.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        existing.InternalStatus = mergedStatus;
        existing.PlatformStatus = external.ExternalStatus;
        existing.CustomerName = external.CustomerName;
        existing.TotalAmount = external.Total;
        existing.DeliveryFee = external.DeliveryFee;
        existing.ServiceFee = external.ServiceFee;
        existing.PaymentMethod = external.PaymentMethod;
        existing.PaymentStatus = external.PaymentStatus;
        existing.RawPayloadJson = external.RawPayloadJson;
        existing.CreatedAtPlatform = external.OrderedAtUtc;
        existing.ExternalOrderCode = external.ExternalOrderCode;
        existing.CustomerPhone = external.CustomerPhone;
        existing.CustomerAddress = external.CustomerAddress;
        existing.CustomerNote = NormalizeCustomerNote(external.CustomerNote);
        existing.UpdatedAt = nowUtc;

        ApplyStatusTransitionTimestamps(
            existing,
            oldStatus,
            mergedStatus,
            nowUtc);

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
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };

            foreach (var opt in itemDto.Options)
            {
                item.Options.Add(new OrderItemOption
                {
                    Name = opt.Name,
                    Price = opt.Price,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                });
            }

            db.OrderItems.Add(item);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

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

    private static Order MapToOrderEntity(ExternalOrderDto external, string idempotencyKey, DateTime receivedAtUtc, OrderStatus internalStatus, DateTime nowUtc)
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
            ReceivedAt = receivedAtUtc,
            RawPayloadJson = external.RawPayloadJson,
            ExternalOrderCode = external.ExternalOrderCode,
            CustomerPhone = external.CustomerPhone,
            CustomerAddress = external.CustomerAddress,
            CustomerNote = NormalizeCustomerNote(external.CustomerNote)
        };

        // Initial milestone timestamps based on initial status
        ApplyStatusTransitionTimestamps(order, OrderStatus.New, internalStatus, nowUtc);

        foreach (var itemDto in external.Items)
        {
            var item = new OrderItem
            {
                ProductName = itemDto.ProductName,
                Quantity = itemDto.Quantity,
                UnitPrice = itemDto.UnitPrice,
                TotalPrice = itemDto.TotalPrice,
                Notes = itemDto.Notes,
            };

            foreach (var opt in itemDto.Options)
            {
                item.Options.Add(new OrderItemOption
                {
                    Name = opt.Name,
                    Price = opt.Price
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

    private static void ApplyStatusTransitionTimestamps(Order order, OrderStatus oldStatus, OrderStatus newStatus, DateTime nowUtc)
    {
        if (oldStatus == newStatus) return;

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

        if (existing.CreatedAtPlatform != external.OrderedAtUtc)
            return false;

        return ItemsSemanticallyEqual(persistedItems, external.Items);
    }

    private static async Task<IReadOnlyList<PersistedOrderItemSnapshot>> LoadPersistedItemSnapshotsAsync(
        TenantDbContext db,
        Guid orderId,
        CancellationToken ct)
    {
        var rows = await db.OrderItems
            .AsNoTracking()
            .Where(i => i.OrderId == orderId)
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

    private static bool ItemsSemanticallyEqual(
        IReadOnlyList<PersistedOrderItemSnapshot> persistedItems,
        IReadOnlyCollection<ExternalOrderItemDto>? incomingItems)
    {
        var incoming = incomingItems ?? [];
        if (persistedItems.Count != incoming.Count)
            return false;

        var left = persistedItems.Select(ToComparableItem).ToList();
        var right = incoming.Select(ToComparableItem).ToList();
        left.Sort(CompareItems);
        right.Sort(CompareItems);

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

    private static ComparableOption[] NormalizeOptions(IEnumerable<(string Name, decimal Price)> options) =>
        options
            .Select(o => new ComparableOption(o.Name ?? string.Empty, NormalizeMoney(o.Price)))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .ThenBy(o => o.Price)
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

