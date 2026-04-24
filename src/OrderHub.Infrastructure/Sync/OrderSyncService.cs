using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Application.Abstractions.Persistence;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Platform.Dtos;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;
using Polly;
using Polly.Retry;

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
                UseJitter = false,
                ShouldHandle = new PredicateBuilder<IReadOnlyCollection<ExternalOrderDto>>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
            })
            .Build();

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
        var dbBase = await _customerDbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        if (dbBase is not CustomerDbContext db)
        {
            throw new InvalidOperationException($"Customer DB factory returned '{dbBase.GetType().Name}' (expected CustomerDbContext).");
        }

        var now = DateTime.UtcNow;
        var connections = await db.PlatformConnections
            .Where(c => c.IsActive && (c.CircuitOpenUntil == null || c.CircuitOpenUntil <= now))
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

        db.SyncLogs.Add(syncLog);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

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

    private async Task<(bool Inserted, bool Updated)> UpsertOrderAsync(CustomerDbContext db, ExternalOrderDto external, CancellationToken ct)
    {
        // Idempotency key (MUST match README exactly)
        var input = $"{external.Platform}:{external.ExternalOrderId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var key = Convert.ToBase64String(hash);

        var existing = await db.Orders
            .Include(o => o.Items).ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var order = MapToOrderEntity(external, key, receivedAtUtc: DateTime.UtcNow);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (Inserted: true, Updated: false);
        }

        // Update status/totals/timestamps; delete-and-insert items for MVP
        existing.PlatformStatus = external.ExternalStatus;
        existing.InternalStatus = _statusMapper.MapToInternalStatus(external.Platform, external.ExternalStatus);
        existing.CustomerName = external.CustomerName;
        existing.DeliveryFee = external.DeliveryFee;
        existing.TotalAmount = external.Total;
        existing.PaymentMethod = external.PaymentMethod;
        existing.PaymentStatus = external.PaymentStatus;
        existing.RawPayloadJson = external.RawPayloadJson;

        existing.CreatedAtPlatform = external.OrderedAtUtc;

        // Replace items/options (MVP approach)
        if (existing.Items.Count > 0)
        {
            db.OrderItemOptions.RemoveRange(existing.Items.SelectMany(i => i.Options));
            db.OrderItems.RemoveRange(existing.Items);
            existing.Items.Clear();
        }

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

            existing.Items.Add(item);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (Inserted: false, Updated: true);
    }

    private Order MapToOrderEntity(ExternalOrderDto external, string idempotencyKey, DateTime receivedAtUtc)
    {
        var order = new Order
        {
            Platform = external.Platform,
            ExternalOrderId = external.ExternalOrderId,
            ExternalOrderCode = external.ExternalOrderId,
            IdempotencyKey = idempotencyKey,
            InternalStatus = _statusMapper.MapToInternalStatus(external.Platform, external.ExternalStatus),
            PlatformStatus = external.ExternalStatus,
            CustomerName = external.CustomerName,
            TotalAmount = external.Total,
            DeliveryFee = external.DeliveryFee,
            ServiceFee = 0m,
            PaymentMethod = external.PaymentMethod,
            PaymentStatus = external.PaymentStatus,
            CreatedAtPlatform = external.OrderedAtUtc,
            ReceivedAt = receivedAtUtc,
            RawPayloadJson = external.RawPayloadJson
        };

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

    private static string SanitizeErrorMessage(Exception ex)
    {
        // Keep message short and avoid leaking sensitive details.
        var msg = ex.Message ?? "Unknown error";
        msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length <= 500 ? msg : msg[..500];
    }
}

