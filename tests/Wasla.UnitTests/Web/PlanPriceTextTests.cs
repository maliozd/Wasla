using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.Web;
using Wasla.Web.Formatting;

namespace Wasla.UnitTests.Web;

/// <summary>
/// How a Wasla plan price reads on customer-facing pages (WAS-147), with the real resources in every culture: monthly
/// and yearly are worded from their own prices, a missing price says it is not set and never shows an amount, and a
/// yearly saving is claimed only when the configured prices show one.
/// </summary>
public sealed class PlanPriceTextTests
{
    private static readonly IStringLocalizer L = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
            NullLoggerFactory.Instance)
        .Create(typeof(SharedResource));

    public static TheoryData<string> Cultures => new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void APrice_ReadsAsItsAmountPerItsOwnPeriod_WithItsVatChoice(string culture)
    {
        using var _ = new CultureScope(culture);
        var monthly = Quote("Monthly", 1200m, vatIncluded: true);
        var yearly = Quote("Yearly", 12345.50m, vatIncluded: false);
        var c = CultureInfo.GetCultureInfo(culture);

        Assert.Equal(
            string.Format(c, L["Pricing.PricePerMonth"].Value, 1200m.ToString("N0", c) + " " + L["Billing.CurrencyTry"].Value),
            PlanPriceText.PerPeriod(monthly, "Monthly", L));
        Assert.Equal(
            string.Format(c, L["Pricing.PricePerYear"].Value, 12345.50m.ToString("N2", c) + " " + L["Billing.CurrencyTry"].Value),
            PlanPriceText.PerPeriod(yearly, "Yearly", L));
        Assert.Equal(L["Billing.VatIncluded"].Value, PlanPriceText.Vat(monthly, L));
        Assert.Equal(L["Billing.VatExcluded"].Value, PlanPriceText.Vat(yearly, L));
        Assert.All(new[] { "Billing.PriceNotSet", "Pricing.PricePerMonth", "Pricing.PricePerYear", "Pricing.YearlySaving", "Billing.CurrencyTry" },
            key => Assert.False(L[key].ResourceNotFound, $"{key} missing in {culture}"));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void AMissingPrice_SaysItIsNotSet_AndCarriesNoAmountOrVat(string culture)
    {
        using var _ = new CultureScope(culture);

        Assert.Equal(L["Billing.PriceNotSet"].Value, PlanPriceText.PerPeriod(null, "Monthly", L));
        Assert.Equal(L["Billing.PriceNotSet"].Value, PlanPriceText.PerPeriod(null, "Yearly", L));
        Assert.Null(PlanPriceText.Vat(null, L));
        Assert.DoesNotContain(L["Billing.CurrencyTry"].Value, PlanPriceText.PerPeriod(null, "Monthly", L), StringComparison.Ordinal);
    }

    [Fact]
    public void AYearlySaving_IsClaimedOnlyWhenTheConfiguredPricesShowOne()
    {
        using var _ = new CultureScope("tr-TR");
        var monthly = Quote("Monthly", 300m, vatIncluded: true);

        // 12 x 300 = 3,600: a yearly 2,750 saves 850, which the claim states exactly.
        Assert.Equal(
            string.Format(CultureInfo.GetCultureInfo("tr-TR"), L["Pricing.YearlySaving"].Value, "850 TL"),
            PlanPriceText.YearlySaving(monthly, Quote("Yearly", 2750m, vatIncluded: true), L));

        Assert.Null(PlanPriceText.YearlySaving(monthly, Quote("Yearly", 3600m, vatIncluded: true), L));
        Assert.Null(PlanPriceText.YearlySaving(monthly, Quote("Yearly", 4000m, vatIncluded: true), L));
        Assert.Null(PlanPriceText.YearlySaving(monthly, Quote("Yearly", 2750m, vatIncluded: false), L));
        Assert.Null(PlanPriceText.YearlySaving(monthly, null, L));
        Assert.Null(PlanPriceText.YearlySaving(null, Quote("Yearly", 2750m, vatIncluded: true), L));
        Assert.Null(PlanPriceText.YearlySaving(monthly, Quote("Yearly", 2750m, vatIncluded: true) with { Price = new BillingPrice(2750m, "EUR") }, L));
    }

    [Fact]
    public void Find_ReturnsOnlyThePriceOfThatPlanAndPeriod()
    {
        var prices = new[] { Quote("Monthly", 300m, true), Quote("Yearly", 2750m, true) with { PlanCode = "Pro" } };

        Assert.Equal(300m, PlanPriceText.Find(prices, "Starter", "Monthly")!.Price.Amount);
        Assert.Null(PlanPriceText.Find(prices, "Starter", "Yearly"));
        Assert.Null(PlanPriceText.Find(prices, "ProPlus", "Monthly"));
    }

    private static BillingPriceQuote Quote(string period, decimal amount, bool vatIncluded) =>
        new(Guid.NewGuid(), "Starter", period, new BillingPrice(amount, "TRY"), vatIncluded);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string culture)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
