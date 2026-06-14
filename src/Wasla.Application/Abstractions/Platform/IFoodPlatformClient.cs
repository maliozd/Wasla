using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Application.Platform.Dtos;

namespace Wasla.Application.Abstractions.Platform;

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

