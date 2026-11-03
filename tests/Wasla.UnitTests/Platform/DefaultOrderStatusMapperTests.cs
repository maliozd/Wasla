using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Accepting an order starts its preparation (WAS-185): every acceptance status maps to Preparing, statuses that are
/// not an acceptance keep their meaning, and no platform status maps to the legacy Accepted any more.
/// </summary>
public sealed class DefaultOrderStatusMapperTests
{
    private readonly DefaultOrderStatusMapper _mapper = new(NullLogger<DefaultOrderStatusMapper>.Instance);

    [Theory]
    // Trendyol GO: Picking is the seller's acceptance (the `picked` call); there is no separate prepare status.
    [InlineData(FoodPlatform.TrendyolYemek, "Created", OrderStatus.New)]
    [InlineData(FoodPlatform.TrendyolYemek, "Picking", OrderStatus.Preparing)]
    [InlineData(FoodPlatform.TrendyolYemek, "Invoiced", OrderStatus.ReadyForPickup)]
    [InlineData(FoodPlatform.TrendyolYemek, "Shipped", OrderStatus.OnTheWay)]
    [InlineData(FoodPlatform.TrendyolYemek, "Onaylandı", OrderStatus.Preparing)]
    [InlineData(FoodPlatform.TrendyolYemek, "Hazırlanıyor", OrderStatus.Preparing)]
    // Yemeksepeti: RECEIVED is the order arriving, not an acceptance.
    [InlineData(FoodPlatform.Yemeksepeti, "RECEIVED", OrderStatus.New)]
    [InlineData(FoodPlatform.Yemeksepeti, "accepted", OrderStatus.Preparing)]
    [InlineData(FoodPlatform.Yemeksepeti, "confirmed", OrderStatus.Preparing)]
    [InlineData(FoodPlatform.Yemeksepeti, "READY_FOR_PICKUP", OrderStatus.ReadyForPickup)]
    // Getir (mock only): VERIFY is what the mock echoes for the accept call.
    [InlineData(FoodPlatform.GetirYemek, "CREATED", OrderStatus.New)]
    [InlineData(FoodPlatform.GetirYemek, "VERIFY", OrderStatus.Preparing)]
    [InlineData(FoodPlatform.GetirYemek, "ACCEPTED", OrderStatus.Preparing)]
    [InlineData(FoodPlatform.GetirYemek, "PREPARE", OrderStatus.Preparing)]
    public void AcceptanceStatuses_StartPreparation(FoodPlatform platform, string externalStatus, OrderStatus expected)
    {
        Assert.Equal(expected, _mapper.MapToInternalStatus(platform, externalStatus));
    }

    [Theory]
    [InlineData(FoodPlatform.TrendyolYemek, new[] { "Created", "Picking", "Invoiced", "Shipped", "Delivered", "Cancelled", "UnSupplied", "Returned", "Yeni", "Onaylandı", "Onaylandi", "Hazırlanıyor", "Hazır", "Yolda", "Teslim edildi", "İptal" })]
    [InlineData(FoodPlatform.Yemeksepeti, new[] { "RECEIVED", "READY_FOR_PICKUP", "DISPATCHED", "DELIVERED", "CANCELLED", "new", "confirmed", "accepted", "preparing", "ready", "courier", "delivered", "failed" })]
    [InlineData(FoodPlatform.GetirYemek, new[] { "CREATED", "VERIFY", "VERIFIED", "CONFIRMED", "ACCEPTED", "PREPARE", "PREPARING", "READY", "HANDOVER", "DELIVERED", "CANCELLED", "FAILED" })]
    public void NoPlatformStatus_MapsToTheLegacyAccepted(FoodPlatform platform, string[] externalStatuses)
    {
        Assert.All(externalStatuses, status => Assert.NotEqual(OrderStatus.Accepted, _mapper.MapToInternalStatus(platform, status)));
    }

    [Fact]
    public void LegacyAccepted_IsHandledAsPreparing_AndNothingElseIs()
    {
        Assert.Equal(OrderStatus.Preparing, OrderPreparation.Displayed(OrderStatus.Accepted));
        Assert.True(OrderPreparation.IsPreparing(OrderStatus.Accepted));
        Assert.True(OrderPreparation.IsPreparing(OrderStatus.Preparing));
        foreach (var status in Enum.GetValues<OrderStatus>().Where(s => s is not (OrderStatus.Accepted or OrderStatus.Preparing)))
        {
            Assert.Equal(status, OrderPreparation.Displayed(status));
            Assert.False(OrderPreparation.IsPreparing(status));
        }

        // The persisted values are unchanged: rows and API values stored before WAS-185 keep their meaning.
        Assert.Equal(1, (int)OrderStatus.Accepted);
        Assert.Equal(2, (int)OrderStatus.Preparing);
    }
}
