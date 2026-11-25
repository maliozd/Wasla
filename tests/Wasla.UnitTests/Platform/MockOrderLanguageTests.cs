using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Signup;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform;
using Wasla.Integrations.Platform.Mock;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The mock clients moved to the provider library without Orders' helpers (WAS-233). What Orders hands them must mean
/// what it meant before: the text language follows Orders' receipt language rule (<see cref="ReceiptLanguageCodes"/>,
/// with Russian written in English, as the mocks have no Russian text), and a tenant's business subtype codes select the
/// same scenario families (<see cref="MockScenarioNames"/>, through <see cref="BusinessSubtypeCatalog"/>).
/// </summary>
public sealed class MockOrderLanguageTests
{
    public static TheoryData<string?> Cultures => new(
        null, "", "   ",
        "tr", "TR", "tr-TR", "tr-tr", " tr ",
        "en", "EN", "en-US", "en-us", "en-GB", " en-gb ",
        "ar", "ar-SA", "AR-sa",
        "ru", "ru-RU", "RU",
        "de", "de-DE", "fr-FR", "en-AU", "ar-EG", "tr-CY", "english", "x");

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TheMockLanguage_IsTheReceiptLanguage_WithRussianWrittenInEnglish(string? culture)
    {
        var receipt = ReceiptLanguageCodes.Normalize(culture);
        var expected = receipt == ReceiptLanguageCodes.Russian ? ReceiptLanguageCodes.English : receipt;

        Assert.Equal(expected, MockOrderText.Language(culture));
    }

    [Fact]
    public void EveryCatalogSubtypeCode_NamesItsOwnScenarioFamily()
    {
        foreach (var definition in BusinessSubtypeCatalog.All)
        {
            var name = Assert.Single(MockScenarioNames.ForSubtypeCodes([definition.Code]));
            Assert.Equal(definition.Subtype.ToString(), name);
            Assert.Contains(name, MockSubtypeOrders.FamilyNames);
        }

        Assert.Equal(Enum.GetValues<BusinessSubtype>().Length, BusinessSubtypeCatalog.All.Count);
    }

    [Fact]
    public void SubtypeCodes_KeepTheirOrderAndRepeats_AndUnknownCodesAreLeftOut()
    {
        Assert.Equal(
            ["Burger", "Pizza", "Burger", "WaffleCrepe"],
            MockScenarioNames.ForSubtypeCodes(["burger", "restaurant", "pizza", "burger", "not-a-subtype", "waffle-crepe"]));
        Assert.Empty(MockScenarioNames.ForSubtypeCodes([]));
        Assert.Empty(MockScenarioNames.ForSubtypeCodes(["restaurant"]));
    }
}
