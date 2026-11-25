using Wasla.Infrastructure.Platform;
using Wasla.Integrations.Platform;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The one comparison of provider identifiers (WAS-232). Ingestion's connection check (<c>OrderIngestionTests</c>) and the
/// webhook pilot's seller and store checks both use it.
/// </summary>
public sealed class ProviderIdentifierTests
{
    [Theory]
    [InlineData("123", "123", true)]
    [InlineData("00123", "123", true)]
    [InlineData(" 123 ", "0123", true)]
    [InlineData("0", "000", true)]
    [InlineData("abc", " abc ", true)]
    [InlineData("123", "124", false)]
    [InlineData("0123a", "123a", false)]
    [InlineData("ABC", "abc", false)]
    [InlineData("١٢٣", "123", false)]
    public void Same_TrimsAndComparesAllDigitValuesAsNumbers(string first, string second, bool same)
    {
        Assert.Equal(same, ProviderIdentifier.Same(first, second));
        Assert.Equal(same, ProviderIdentifier.Same(second, first));
    }

    [Theory]
    [InlineData("0900001", "0900002", true)]
    [InlineData(" 900001 ", "900002", true)]
    [InlineData("900001", "900003", false)]
    public void ThePilot_RecognizesItsSellerAndStore_WithTheSameComparison(string seller, string store, bool recognized)
    {
        var pilot = new TrendyolGoMealWebhookPilotOptions { SellerId = "900001", StoreId = "900002" };

        Assert.True(pilot.IsPilotSeller(seller));
        Assert.Equal(recognized, pilot.IsPilotStore(store));
        Assert.Equal(ProviderIdentifier.Same(store, "900002"), pilot.IsPilotStore(store));
    }
}
