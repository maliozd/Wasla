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
}

