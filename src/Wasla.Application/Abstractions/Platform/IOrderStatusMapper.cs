using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Platform;

public interface IOrderStatusMapper
{
    OrderStatus MapToInternalStatus(FoodPlatform platform, string externalStatus);
}

