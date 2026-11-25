using Wasla.Application.Orders;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform;
using Wasla.Integrations.Platform;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The reasons an operator may choose (<see cref="OrderRejectReasons"/>, Orders) are exactly the reasons the provider
/// accepts (<see cref="ProviderRejectReasons"/>, the provider library), in the provider's order, for every platform
/// (WAS-233). Application cannot reference the library, so Orders keeps its list and this test keeps the two equal.
/// </summary>
public sealed class OrderRejectReasonParityTests
{
    public static TheoryData<FoodPlatform> Platforms => new(Enum.GetValues<FoodPlatform>().Append((FoodPlatform)99));

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TheOperatorsReasons_AreTheProvidersReasons(FoodPlatform platform)
    {
        Assert.Equal(ProviderRejectReasons.For(platform.ToProviderPlatform()), OrderRejectReasons.For(platform));
    }

    [Fact]
    public void TrendyolGo_HasItsDocumentedReasons()
    {
        Assert.Equal([621, 622, 623, 627], OrderRejectReasons.For(FoodPlatform.TrendyolYemek));
        Assert.Equal(ProviderRejectReasons.TrendyolGo, OrderRejectReasons.For(FoodPlatform.TrendyolYemek));
    }
}
