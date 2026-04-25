using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        IOrderStatusMapper statusMapper)
    {
        _customerDbFactory = customerDbFactory;
        _platformClients = platformClients;
        _statusMapper = statusMapper;
    }

    public async Task SyncCustomerAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var connections = await db.PlatformConnections
            .Where(c =>
                c.IsActive &&
                (c.CircuitOpenUntil == null || c.CircuitOpenUntil <= now) &&
                (c.LastSyncAttempt == null ||
                 c.LastSyncAttempt.Value.AddSeconds(c.SyncIntervalSeconds) <= now))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var connection in connections)
        {
            await SyncConnectionAsync(db, connection, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncConnectionAsync(CustomerDbContext db, PlatformConnection connection, CancellationToken ct)
    {
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

            var externalOrders = await _fetchPipeline.ExecuteAsync(
                    async token => await client.FetchOrdersAsync(connection, token).ConfigureAwait(false),
                    ct)
                .ConfigureAwait(false);

            syncLog.OrdersFetched = externalOrders.Count;

            foreach (var external in externalOrders)
            {
                var (inserted, updated) = await UpsertOrderAsync(db, external, ct).ConfigureAwait(false);
                if (inserted) syncLog.OrdersInserted++;
                if (updated) syncLog.OrdersUpdated++;
            }

            connection.ConsecutiveFailures = 0;
            connection.CircuitOpenUntil = null;
            connection.LastSuccessfulSync = DateTime.UtcNow;

            syncLog.Status = SyncStatus.Success;
            syncLog.FinishedAt = DateTime.UtcNow;

            db.SyncLogs.Add(syncLog);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
        }
    }

    private async Task<(bool Inserted, bool Updated)> UpsertOrderAsync(
      CustomerDbContext db,
      ExternalOrderDto external,
      CancellationToken ct)
    {
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

            return (Inserted: true, Updated: false);
        }

        var oldStatus = existing.InternalStatus;

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

        return (Inserted: false, Updated: true);
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

