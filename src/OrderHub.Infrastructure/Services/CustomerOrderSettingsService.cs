using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Infrastructure.Persistence.Tenant;

namespace OrderHub.Infrastructure.Services;

public sealed class CustomerOrderSettingsService : ICustomerOrderSettingsService
{
    private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IValidator<UpdateCustomerOrderSettingsCommand> _validator;
    private readonly ILogger<CustomerOrderSettingsService> _logger;

    public CustomerOrderSettingsService(
        ITenantDbContextFactory dbFactory,
        IValidator<UpdateCustomerOrderSettingsCommand> validator,
        ILogger<CustomerOrderSettingsService> logger)
    {
        _dbFactory = dbFactory;
        _validator = validator;
        _logger = logger;
    }

    public async Task<CustomerOrderSettingsResult> GetAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.CustomerOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            _logger.LogDebug(
                "Order settings row missing; returning defaults. CustomerId={CustomerId}",
                customerId);
            return DefaultResult();
        }

        _logger.LogDebug(
            "Order settings loaded. CustomerId={CustomerId}, AutoApproveNewOrders={AutoApproveNewOrders}, AutoPrintReceiptOnAutoApprove={AutoPrintReceiptOnAutoApprove}, ReceiptPrintCopyCount={ReceiptPrintCopyCount}",
            customerId,
            row.AutoApproveNewOrders,
            row.AutoPrintReceiptOnAutoApprove,
            row.ReceiptPrintCopyCount);

        return Map(row);
    }

    public async Task<CustomerOrderSettingsResult> UpdateAsync(
        Guid customerId,
        UpdateCustomerOrderSettingsCommand command,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(command, ct).ConfigureAwait(false);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.CustomerOperationalSettings
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new CustomerOperationalSettings
            {
                Id = SingletonId,
                OrderSyncEnabled = true,
                AutoApproveNewOrders = command.AutoApproveNewOrders,
                AutoPrintReceiptOnAutoApprove = command.AutoPrintReceiptOnAutoApprove,
                ReceiptPrintCopyCount = command.ReceiptPrintCopyCount,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.CustomerOperationalSettings.Add(row);
        }
        else
        {
            row.AutoApproveNewOrders = command.AutoApproveNewOrders;
            row.AutoPrintReceiptOnAutoApprove = command.AutoPrintReceiptOnAutoApprove;
            row.ReceiptPrintCopyCount = command.ReceiptPrintCopyCount;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Order settings updated. CustomerId={CustomerId}, AutoApproveNewOrders={AutoApproveNewOrders}, AutoPrintReceiptOnAutoApprove={AutoPrintReceiptOnAutoApprove}, ReceiptPrintCopyCount={ReceiptPrintCopyCount}",
            customerId,
            row.AutoApproveNewOrders,
            row.AutoPrintReceiptOnAutoApprove,
            row.ReceiptPrintCopyCount);

        return Map(row);
    }

    private static CustomerOrderSettingsResult DefaultResult() =>
        new(AutoApproveNewOrders: false, AutoPrintReceiptOnAutoApprove: false, ReceiptPrintCopyCount: 1);

    private static CustomerOrderSettingsResult Map(CustomerOperationalSettings row) =>
        new(row.AutoApproveNewOrders, row.AutoPrintReceiptOnAutoApprove, row.ReceiptPrintCopyCount);
}
