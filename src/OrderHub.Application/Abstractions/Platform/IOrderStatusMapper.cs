using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Platform;

public interface IOrderStatusMapper
{
    OrderStatus MapToInternalStatus(FoodPlatform platform, string externalStatus);
}

