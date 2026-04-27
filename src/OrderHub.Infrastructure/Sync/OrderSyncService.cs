using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace OrderHub.Infrastructure.Sync;

public sealed class OrderSyncService : IOrderSyncService
{
    private readonly ICustomerDbContextFactory _customerDbFactory;
    private readonly IEnumerable<IFoodPlatformClient> _platformClients;
    private readonly IOrderStatusMapper _statusMapper;
    private readonly ILogger<OrderSyncService> _logger;

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

    //.AddCircuitBreaker(new CircuitBreakerStrategyOptions<IReadOnlyCollection<ExternalOrderDto>>
    //{
    //    FailureRatio = 0.5,
    //    SamplingDuration = TimeSpan.FromSeconds(30),
    //    MinimumThroughput = 5,
    //    BreakDuration = TimeSpan.FromSeconds(30),
    //    ShouldHandle = new PredicateBuilder<IReadOnlyCollection<ExternalOrderDto>>()
    //        .Handle<HttpRequestException>()
    //        .Handle<TimeoutException>()
    //        .Handle<TimeoutRejectedException>()
    //})


    public OrderSyncService(
        ICustomerDbContextFactory customerDbFactory,
        IEnumerable<IFoodPlatformClient> platformClients,
        IOrderStatusMapper statusMapper,
        ILogger<OrderSyncService> logger)
    {
        _customerDbFactory = customerDbFactory;
        _platformClients = platformClients;
        _statusMapper = statusMapper;
        _logger = logger;
    }

    public async Task SyncCustomerAsync(Guid customerId, CancellationToken ct)
    {
        await SyncCustomerWithResultAsync(customerId, ct).ConfigureAwait(false);
    }

    public async Task<OrderSyncCustomerResult> SyncCustomerWithResultAsync(Guid customerId, CancellationToken ct)
    {
        var swCustomer = Stopwatch.StartNew();
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

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
            // Usually frequent; keep it Info but concise.
            _logger.LogInformation(
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

        foreach (var connection in dueConnections)
        {
            var result = await SyncConnectionAsync(customerId, db, connection, ct).ConfigureAwait(false);
            fetched += result.FetchedCount;
            inserted += result.InsertedCount;
            updated += result.UpdatedCount;
            skipped += result.SkippedCount;
            unchanged += result.UnchangedCount;
            if (result.IsFailed) failedConnections++;
        }

        swCustomer.Stop();
        _logger.LogInformation(
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

        return new OrderSyncCustomerResult(
            customerId,
            dueConnections.Count,
            fetched,
            inserted,
            updated,
            skipped,
            unchanged,
            failedConnections);
    }

    private async Task<OrderSyncConnectionResult> SyncConnectionAsync(
        Guid customerId,
        CustomerDbContext db,
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

        try
        {
            var client = _platformClients.FirstOrDefault(c => c.Platform == connection.Platform);
            if (client is null)
            {
                throw new InvalidOperationException($"No IFoodPlatformClient registered for platform '{connection.Platform}'.");
            }

            _logger.LogInformation(
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

            // Provider-specific required config (recoverable): warn and short-circuit this connection
            // to avoid repeating failures every cycle.
            if (connection.Platform == FoodPlatform.TrendyolYemek && string.IsNullOrWhiteSpace(connection.SupplierId))
            {
                _logger.LogWarning(
                    "Skipping {Platform} connection {ConnectionId} because required setting {SettingKey} is missing. CustomerId={CustomerId}, StoreId={StoreId}",
                    connection.Platform,
                    connection.Id,
                    "SupplierId",
                    customerId,
                    connection.StoreId);

                syncLog.Status = SyncStatus.Failed;
                syncLog.FinishedAt = DateTime.UtcNow;
                syncLog.ErrorMessage = "Missing required setting: SupplierId";
                db.SyncLogs.Add(syncLog);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                swConn.Stop();
                return new OrderSyncConnectionResult(customerId, connection.Id, connection.Platform, connection.StoreId, 0, 0, 0, 0, 0, IsFailed: true);
            }

            var swFetch = Stopwatch.StartNew();
            var externalOrders = await _fetchPipeline.ExecuteAsync(
                    async token => await client.FetchOrdersAsync(connection, token).ConfigureAwait(false),
                    ct)
                .ConfigureAwait(false);
            swFetch.Stop();

            syncLog.OrdersFetched = externalOrders.Count;

            _logger.LogInformation(
                "Provider returned {OrderCount} orders. Platform={Platform}, StoreId={StoreId}, ConnectionId={ConnectionId}, CustomerId={CustomerId}, FetchElapsedMs={FetchElapsedMs}, SampleExternalOrderIds={SampleExternalOrderIds}",
                externalOrders.Count,
                connection.Platform,
                connection.StoreId,
                connection.Id,
                customerId,
                swFetch.ElapsedMilliseconds,
                externalOrders.Select(x => x.ExternalOrderId).Where(x => !string.IsNullOrWhiteSpace(x)).Take(10).ToArray());

            var connInserted = 0;
            var connUpdated = 0;
            var connSkipped = 0;
            var connUnchanged = 0;

            var swUpsert = Stopwatch.StartNew();
            foreach (var external in externalOrders)
            {
                var r = await UpsertOrderAsync(db, external, ct).ConfigureAwait(false);
                if (r.Inserted) { syncLog.OrdersInserted++; connInserted++; }
                if (r.Updated) { syncLog.OrdersUpdated++; connUpdated++; }
                if (r.Skipped) { connSkipped++; }
                if (r.Unchanged) { connUnchanged++; }
            }
            swUpsert.Stop();

            connection.ConsecutiveFailures = 0;
            connection.CircuitOpenUntil = null;
            connection.LastSuccessfulSync = DateTime.UtcNow;

            syncLog.Status = SyncStatus.Success;
            syncLog.FinishedAt = DateTime.UtcNow;

            db.SyncLogs.Add(syncLog);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            swConn.Stop();
            _logger.LogInformation(
                "Completed platform sync. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, Fetched={FetchedCount}, Inserted={InsertedCount}, Updated={UpdatedCount}, Skipped={SkippedCount}, Unchanged={UnchangedCount}, UpsertElapsedMs={UpsertElapsedMs}, ElapsedMs={ElapsedMs}",
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                externalOrders.Count,
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
                externalOrders.Count,
                connInserted,
                connUpdated,
                connSkipped,
                connUnchanged,
                IsFailed: false);
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
                "Platform connection sync failed. CustomerId={CustomerId}, ConnectionId={ConnectionId}, Platform={Platform}, StoreId={StoreId}, ElapsedMs={ElapsedMs}",
                customerId,
                connection.Id,
                connection.Platform,
                connection.StoreId,
                swConn.ElapsedMilliseconds);

            return new OrderSyncConnectionResult(customerId, connection.Id, connection.Platform, connection.StoreId, syncLog.OrdersFetched, syncLog.OrdersInserted, syncLog.OrdersUpdated, 0, 0, IsFailed: true);
        }
    }

    private async Task<OrderUpsertResult> UpsertOrderAsync(
      CustomerDbContext db,
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

            return new OrderUpsertResult(true, false, false, false, order.ExternalOrderId);
        }

        var oldStatus = existing.InternalStatus;
        var oldTotal = existing.TotalAmount;
        var oldPlatformStatus = existing.PlatformStatus;

        await using var tx = await db.Database.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        existing.InternalStatus = newStatus;
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
        existing.UpdatedAt = nowUtc;

        ApplyStatusTransitionTimestamps(
            existing,
            oldStatus,
            newStatus,
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

        _logger.LogDebug(
            "Updated order {ExternalOrderId}. Status {OldStatus} -> {NewStatus}, Total {OldTotal} -> {NewTotal}, PlatformStatus {OldPlatformStatus} -> {NewPlatformStatus}",
            existing.ExternalOrderId,
            oldStatus,
            newStatus,
            oldTotal,
            existing.TotalAmount,
            oldPlatformStatus,
            existing.PlatformStatus);

        return new OrderUpsertResult(false, true, false, false, existing.ExternalOrderId);
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
            CustomerAddress = external.CustomerAddress
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

    private static string SanitizeErrorMessage(Exception ex)
    {
        // Keep message short and avoid leaking sensitive details.
        var msg = ex.Message ?? "Unknown error";
        msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length <= 500 ? msg : msg[..500];
    }
}

