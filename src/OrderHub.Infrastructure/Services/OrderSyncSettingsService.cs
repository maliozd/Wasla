using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class OrderSyncSettingsService : IOrderSyncSettingsService
{
    private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ICustomerDbContextFactory _dbFactory;

    public OrderSyncSettingsService(ICustomerDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<OrderSyncSettingsResult> GetAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.CustomerOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        // If settings row doesn't exist, preserve existing behavior: sync is enabled.
        return new OrderSyncSettingsResult(row?.OrderSyncEnabled ?? true);
    }

    public async Task<OrderSyncSettingsResult> UpdateAsync(Guid customerId, bool enabled, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.CustomerOperationalSettings
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new CustomerOperationalSettings
            {
                Id = SingletonId,
                OrderSyncEnabled = enabled,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.CustomerOperationalSettings.Add(row);
        }
        else
        {
            row.OrderSyncEnabled = enabled;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new OrderSyncSettingsResult(row.OrderSyncEnabled);
    }
}

