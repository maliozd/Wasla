using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

/// <summary>
/// The saved order settings. <see cref="OperationalMode"/> is read from the same row so callers can show what the
/// automation does right now without a second read; it is not an order setting and is never written through this service.
/// </summary>
public sealed record TenantOrderSettingsResult(
    bool AutoApproveNewOrders,
    bool AutoPrintReceiptOnAutoApprove,
    int ReceiptPrintCopyCount,
    TenantOperationalMode OperationalMode = TenantOperationalMode.Live);

public sealed record UpdateTenantOrderSettingsCommand(
    bool AutoApproveNewOrders,
    bool AutoPrintReceiptOnAutoApprove,
    int ReceiptPrintCopyCount);

public interface ITenantOrderSettingsService
{
    Task<TenantOrderSettingsResult> GetAsync(Guid customerId, CancellationToken ct);
    Task<TenantOrderSettingsResult> UpdateAsync(Guid customerId, UpdateTenantOrderSettingsCommand command, CancellationToken ct);
}
