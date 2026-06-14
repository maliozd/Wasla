using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class TenantOrderSettingsService : ITenantOrderSettingsService
{
    private static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IValidator<UpdateTenantOrderSettingsCommand> _validator;
    private readonly ILogger<TenantOrderSettingsService> _logger;

    public TenantOrderSettingsService(
        ITenantDbContextFactory dbFactory,
        IValidator<UpdateTenantOrderSettingsCommand> validator,
        ILogger<TenantOrderSettingsService> logger)
    {
        _dbFactory = dbFactory;
        _validator = validator;
        _logger = logger;
    }

    public async Task<TenantOrderSettingsResult> GetAsync(Guid customerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.TenantOperationalSettings
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

    public async Task<TenantOrderSettingsResult> UpdateAsync(
        Guid customerId,
        UpdateTenantOrderSettingsCommand command,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(command, ct).ConfigureAwait(false);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var row = await db.TenantOperationalSettings
            .FirstOrDefaultAsync(x => x.Id == SingletonId, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new TenantOperationalSettings
            {
                Id = SingletonId,
                OrderSyncEnabled = true,
                AutoApproveNewOrders = command.AutoApproveNewOrders,
                AutoPrintReceiptOnAutoApprove = command.AutoPrintReceiptOnAutoApprove,
                ReceiptPrintCopyCount = command.ReceiptPrintCopyCount,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.TenantOperationalSettings.Add(row);
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

    private static TenantOrderSettingsResult DefaultResult() =>
        new(AutoApproveNewOrders: false, AutoPrintReceiptOnAutoApprove: false, ReceiptPrintCopyCount: 1);

    private static TenantOrderSettingsResult Map(TenantOperationalSettings row) =>
        new(row.AutoApproveNewOrders, row.AutoPrintReceiptOnAutoApprove, row.ReceiptPrintCopyCount);
}
