namespace OrderHub.Application.Abstractions.Orders;

public sealed record CustomerOrderSettingsResult(
    bool AutoApproveNewOrders,
    bool AutoPrintReceiptOnAutoApprove,
    int ReceiptPrintCopyCount);

public sealed record UpdateCustomerOrderSettingsCommand(
    bool AutoApproveNewOrders,
    bool AutoPrintReceiptOnAutoApprove,
    int ReceiptPrintCopyCount);

public interface ITenantOrderSettingsService
{
    Task<CustomerOrderSettingsResult> GetAsync(Guid customerId, CancellationToken ct);
    Task<CustomerOrderSettingsResult> UpdateAsync(Guid customerId, UpdateCustomerOrderSettingsCommand command, CancellationToken ct);
}
