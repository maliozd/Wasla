using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;
using OrderHub.Application.Platform.Dtos;

namespace OrderHub.Application.Abstractions.Platform;

public interface IFoodPlatformClient
{
    FoodPlatform Platform { get; }

    Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(
        PlatformConnection connection,
        CancellationToken ct);

    Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct);
    Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct);
    Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct);
    Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct);
    Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct);
}

