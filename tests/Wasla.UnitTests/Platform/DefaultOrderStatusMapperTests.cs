using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Contracts.Enums;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Accepting an order starts its preparation (WAS-185): every acceptance status maps to Preparing, statuses that are
/// not an acceptance keep their meaning, and the retired Accepted value stays unused.
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
    [InlineData(FoodPlatform.TrendyolYemek, new[] { "Created", "Picking", "Invoiced", "Shipped", "Delivered", "Cancelled", "UnSupplied", "Returned", "Yeni", "Onaylandı", "Onaylandi", "Hazırlanıyor", "Hazır", "Yolda", "Teslim edildi", "İptal", "unknown" })]
    [InlineData(FoodPlatform.Yemeksepeti, new[] { "RECEIVED", "READY_FOR_PICKUP", "DISPATCHED", "DELIVERED", "CANCELLED", "new", "confirmed", "accepted", "preparing", "ready", "courier", "delivered", "failed", "unknown" })]
    [InlineData(FoodPlatform.GetirYemek, new[] { "CREATED", "VERIFY", "VERIFIED", "CONFIRMED", "ACCEPTED", "PREPARE", "PREPARING", "READY", "HANDOVER", "DELIVERED", "CANCELLED", "FAILED", "unknown" })]
    public void EveryPlatformStatus_MapsToADefinedStatus(FoodPlatform platform, string[] externalStatuses)
    {
        Assert.All(externalStatuses, status => Assert.True(Enum.IsDefined(_mapper.MapToInternalStatus(platform, status)), status));
    }

    [Fact]
    public void OrderStatusValues_AreKept_AndTheRetiredAcceptedValueIsUnused()
    {
        // Stored rows and API values keep their numbers. 1 was Accepted until WAS-185 and is never reused.
        var expected = new Dictionary<string, int>
        {
            ["New"] = 0,
            ["Preparing"] = 2,
            ["ReadyForPickup"] = 3,
            ["OnTheWay"] = 4,
            ["Delivered"] = 5,
            ["Cancelled"] = 6,
            ["Failed"] = 7
        };
        Assert.Equal(expected, Enum.GetValues<OrderStatus>().ToDictionary(s => s.ToString(), s => (int)s));
        Assert.Equal(expected, Enum.GetValues<OrderStatusDto>().ToDictionary(s => s.ToString(), s => (int)s));
        Assert.False(Enum.IsDefined(typeof(OrderStatus), 1));
        Assert.False(Enum.IsDefined(typeof(OrderStatusDto), 1));
    }
}
