using System.Text.Json;
using System.Text.Json.Serialization;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform;
using Wasla.Integrations.Platform;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The provider library owns the enums of the provider contract and mirrors the Orders ones (WAS-233, decision D-B):
/// every member of each pair has the same name and numeric value, in both directions, with the same underlying type.
/// Orders converts at its boundary (<see cref="ProviderEnumConversions"/>) by value, so what it stores (the value) and
/// what it shows or writes as JSON (the name or the number) means the same on both sides.
/// </summary>
public sealed class ProviderEnumParityTests
{
    public static TheoryData<Type, Type> Pairs => new()
    {
        { typeof(ProviderPlatform), typeof(FoodPlatform) },
        { typeof(ProviderPaymentMethod), typeof(PaymentMethod) },
        { typeof(ProviderPaymentStatus), typeof(PaymentStatus) }
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryMember_HasTheSameNameAndValue_InBothDirections(Type provider, Type orders)
    {
        Assert.Equal(Enum.GetUnderlyingType(orders), Enum.GetUnderlyingType(provider));
        Assert.Equal(Members(orders), Members(provider));
        Assert.NotEmpty(Members(provider));
    }

    [Fact]
    public void ToFoodPlatform_AndBack_KeepEveryMember()
    {
        foreach (var platform in Enum.GetValues<ProviderPlatform>())
        {
            var orders = platform.ToFoodPlatform();
            Assert.Equal(platform.ToString(), orders.ToString());
            Assert.Equal((int)platform, (int)orders);
            Assert.Equal(platform, orders.ToProviderPlatform());
        }

        foreach (var platform in Enum.GetValues<FoodPlatform>())
            Assert.Equal(platform, platform.ToProviderPlatform().ToFoodPlatform());
    }

    [Fact]
    public void PaymentConversions_KeepEveryMember()
    {
        foreach (var method in Enum.GetValues<ProviderPaymentMethod>())
        {
            Assert.Equal(method.ToString(), method.ToPaymentMethod().ToString());
            Assert.Equal((int)method, (int)method.ToPaymentMethod());
            Assert.Equal(method, method.ToPaymentMethod().ToProviderPaymentMethod());
        }

        foreach (var status in Enum.GetValues<ProviderPaymentStatus>())
        {
            Assert.Equal(status.ToString(), status.ToPaymentStatus().ToString());
            Assert.Equal((int)status, (int)status.ToPaymentStatus());
            Assert.Equal(status, status.ToPaymentStatus().ToProviderPaymentStatus());
        }
    }

    /// <summary>
    /// A value outside the members keeps its number across the boundary, as it did when both sides used the Orders enum,
    /// so no stored value changes meaning or fails on the way.
    /// </summary>
    [Fact]
    public void AValueOutsideTheMembers_KeepsItsNumber()
    {
        Assert.Equal(99, (int)((FoodPlatform)99).ToProviderPlatform());
        Assert.Equal(99, (int)((ProviderPlatform)99).ToFoodPlatform());
        Assert.Equal(-1, (int)((ProviderPaymentMethod)(-1)).ToPaymentMethod());
        Assert.Equal(7, (int)((ProviderPaymentStatus)7).ToPaymentStatus());
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryMember_WritesTheSameJson_AsNumberAndAsName(Type provider, Type orders)
    {
        var asName = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        foreach (var name in Enum.GetNames(orders))
        {
            var ordersValue = Enum.Parse(orders, name);
            var providerValue = Enum.Parse(provider, name);

            Assert.Equal(JsonSerializer.Serialize(ordersValue, orders), JsonSerializer.Serialize(providerValue, provider));
            Assert.Equal(JsonSerializer.Serialize(ordersValue, orders, asName), JsonSerializer.Serialize(providerValue, provider, asName));
            Assert.Equal(providerValue, JsonSerializer.Deserialize(JsonSerializer.Serialize(ordersValue, orders), provider));
            Assert.Equal(ordersValue, JsonSerializer.Deserialize(JsonSerializer.Serialize(providerValue, provider, asName), orders, asName));
        }
    }

    private static List<(string Name, long Value)> Members(Type enumType) =>
        Enum.GetNames(enumType)
            .Select(name => (name, Convert.ToInt64(Enum.Parse(enumType, name))))
            .OrderBy(member => member.Item2)
            .ThenBy(member => member.name, StringComparer.Ordinal)
            .ToList();
}
