using Wasla.Infrastructure.Platform.TrendyolGo;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The provider's single list of the Trendyol GO package statuses Wasla recognizes, and its comparison rule (WAS-232):
/// trimmed, case-sensitive, <c>Returned</c> in any casing, blank neither recognized nor unknown. The client warns from
/// it (<see cref="TrendyolGoUnknownStatusWarningTests"/>) and the Orders mapper maps from it
/// (<see cref="DefaultOrderStatusMapperTests"/>).
/// </summary>
public sealed class TrendyolGoPackageStatusTests
{
    [Fact]
    public void TheDocumentedStatuses_AreTheFetchFilter_InTheDocumentationsOrderAndCasing()
    {
        Assert.Equal(
            ["Created", "Picking", "Invoiced", "Cancelled", "UnSupplied", "Shipped", "Delivered"],
            TrendyolGoPackageStatus.Documented);
        Assert.Equal(
            "Created,Picking,Invoiced,Cancelled,UnSupplied,Shipped,Delivered",
            TrendyolGoFoodPlatformClient.PackageStatusesFilter);
    }

    [Theory]
    [InlineData("Created", "Created")]
    [InlineData("UnSupplied", "UnSupplied")]
    [InlineData(" Picking ", "Picking")]
    [InlineData("\tDelivered\r\n", "Delivered")]
    [InlineData(" Shipped ", "Shipped")]
    [InlineData("Returned", "Returned")]
    [InlineData("RETURNED", "Returned")]
    [InlineData("reTURned", "Returned")]
    [InlineData("new", "New")]
    [InlineData("New", "New")]
    [InlineData("yeni", "Yeni")]
    [InlineData("onaylandi", "Onaylandı")]
    [InlineData("Hazirlaniyor", "Hazırlanıyor")]
    [InlineData("hazir", "Hazır")]
    [InlineData("yolda", "Yolda")]
    [InlineData("teslim", "Teslim")]
    [InlineData("teslim edildi", "Teslim edildi")]
    [InlineData("Iptal", "İptal")]
    [InlineData("iptal edildi", "İptal edildi")]
    [InlineData("basarisiz", "Başarısız")]
    public void ARecognizedSpelling_GivesItsStatus(string raw, string expected)
    {
        Assert.Equal(expected, TrendyolGoPackageStatus.Recognize(raw));
        Assert.Contains(expected, TrendyolGoPackageStatus.All);
    }

    [Theory]
    // Blank is not a status.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    // Case-sensitive: another casing of a known status is not recognized.
    [InlineData("created")]
    [InlineData("PICKING")]
    [InlineData("UNSUPPLIED")]
    [InlineData("Unsupplied")]
    [InlineData("NEW")]
    [InlineData("YENI")]
    [InlineData("Teslim Edildi")]
    [InlineData("İPTAL")]
    // Unknown text, and characters Trim does not remove.
    [InlineData("Returned2")]
    [InlineData("Hazırlandı")]
    [InlineData("Created,Picking")]
    [InlineData("​Created")]
    public void ABlankOrUnknownStatus_IsNotRecognized(string? raw)
    {
        Assert.Null(TrendyolGoPackageStatus.Recognize(raw));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(" Mystery ", "Mystery")]
    [InlineData("\tCREATED\n", "CREATED")]
    public void Normalize_TrimsAndTurnsMissingIntoEmpty(string? raw, string expected)
    {
        Assert.Equal(expected, TrendyolGoPackageStatus.Normalize(raw));
    }

    [Fact]
    public void EveryRecognizedStatus_RecognizesItself_AndIsListedOnce()
    {
        Assert.Equal(TrendyolGoPackageStatus.All.Count, TrendyolGoPackageStatus.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(TrendyolGoPackageStatus.All, status => Assert.Equal(status, TrendyolGoPackageStatus.Recognize(status)));
        Assert.All(TrendyolGoPackageStatus.Documented, status => Assert.Contains(status, TrendyolGoPackageStatus.All));
    }
}
