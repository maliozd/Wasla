namespace OrderHub.Application.Abstractions.Orders;

public sealed record TenantOrderSettingsResult(
    bool AutoApproveNewOrders,
    bool AutoPrintReceiptOnAutoApprove,
    int ReceiptPrintCopyCount);

public sealed record UpdateTenantOrderSettingsCommand(
    bool AutoApproveNewOrders,
    bool AutoPrintReceiptOnAutoApprove,
    int ReceiptPrintCopyCount);

public interface ITenantOrderSettingsService
{
    Task<TenantOrderSettingsResult> GetAsync(Guid customerId, CancellationToken ct);
    Task<TenantOrderSettingsResult> UpdateAsync(Guid customerId, UpdateTenantOrderSettingsCommand command, CancellationToken ct);
}
