using System.Text.RegularExpressions;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mock;

namespace Wasla.UnitTests.Platform;

public sealed class MockOrdersCustomerNameTests
{
    private static readonly Regex PlaceholderCustomerPattern =
        new(@"^Customer\s+\d+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public void CreateCustomerName_UsesRealisticNames_NotPlaceholderPattern()
    {
        var rng = new Random(42);

        for (var i = 0; i < 200; i++)
        {
            var name = MockOrders.CreateCustomerName(rng);

            Assert.False(
                PlaceholderCustomerPattern.IsMatch(name),
                $"Expected a realistic name, got placeholder-like value: '{name}'");
            Assert.Contains(name, MockOrders.CustomerNames);
            Assert.Contains(' ', name);
        }
    }

    [Fact]
    public void CreateOrders_AssignsRealisticCustomerNames()
    {
        var connection = new PlatformConnection
        {
            Platform = FoodPlatform.Yemeksepeti,
            StoreId = "mock-store",
        };

        var orders = MockOrders.CreateOrders(
            FoodPlatform.Yemeksepeti,
            connection,
            count: 80,
            random: new Random(12345));

        Assert.Equal(80, orders.Count);

        foreach (var order in orders)
        {
            Assert.False(
                PlaceholderCustomerPattern.IsMatch(order.CustomerName),
                $"Expected a realistic name, got: '{order.CustomerName}'");
            Assert.Contains(order.CustomerName, MockOrders.CustomerNames);
        }

        // Deterministic seed should produce more than one distinct name across a large sample.
        Assert.True(orders.Select(o => o.CustomerName).Distinct().Count() >= 5);
    }
}
