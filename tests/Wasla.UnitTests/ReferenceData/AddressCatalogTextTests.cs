using Wasla.Infrastructure.ReferenceData;

namespace Wasla.UnitTests.ReferenceData;

/// <summary>WAS-214: the name, comparison and street-type rules of the neighbourhood and street catalog.</summary>
public sealed class AddressCatalogTextTests
{
    [Theory]
    [InlineData("YENİ ", "YENİ")]
    [InlineData("  TEPE  BAĞ", "TEPE BAĞ")]
    [InlineData("AKÖREN\r\nKÜME\tEVLERİ", "AKÖREN KÜME EVLERİ")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_TrimsAndCollapsesWhitespace_AndKeepsTheSpelling(string? value, string expected) =>
        Assert.Equal(expected, AddressCatalogText.Normalize(value));

    [Fact]
    public void Normalize_ComposesDecomposedTurkishLetters()
    {
        var decomposed = "ŞEHİT"; // Ş and İ written as base letter plus combining mark
        Assert.Equal("ŞEHİT", AddressCatalogText.Normalize(decomposed));
    }

    [Theory]
    [InlineData("CAFERAĞA", null)]
    [InlineData("1497", null)]
    [InlineData("KÂHYA (YENİ) 2/3-A", null)]
    [InlineData("", "no name")]
    [InlineData("-", "no letter or digit")]
    [InlineData("--", "no letter or digit")]
    [InlineData("?S?MS?Z", "damaged text (a character was replaced by '?')")]
    [InlineData("DEDEBA�I", "damaged text (a character was replaced by '?')")]
    [InlineData("ΑΘΗΝΑ", "letter outside the Latin script")]
    [InlineData("A​B", "unsupported character")]
    public void Problem_AcceptsTurkishNames_AndNamesWhatCannotBeStored(string name, string? expected) =>
        Assert.Equal(expected, AddressCatalogText.Problem(name, 150));

    [Fact]
    public void Problem_RefusesAName_LongerThanItsColumn()
    {
        Assert.Null(AddressCatalogText.Problem(new string('A', 150), AddressCatalogText.NeighborhoodNameMaxLength));
        Assert.Equal("longer than 150 characters", AddressCatalogText.Problem(new string('A', 151), AddressCatalogText.NeighborhoodNameMaxLength));
    }

    [Theory]
    [InlineData("Kadıköy", "KADIKÖY")]
    [InlineData("İstanbul", "İSTANBUL")]
    [InlineData("Afyonkarahisar", "AFYONKARAHİSAR")]
    public void TurkishKey_MatchesNamesThatDifferOnlyInCase(string wasla, string source) =>
        Assert.Equal(AddressCatalogText.TurkishKey(source), AddressCatalogText.TurkishKey(wasla));

    [Fact]
    public void TurkishKey_KeepsDottedAndDotlessI_Apart()
    {
        Assert.NotEqual(AddressCatalogText.TurkishKey("BARIŞ"), AddressCatalogText.TurkishKey("BARİŞ"));
        Assert.NotEqual(AddressCatalogText.TurkishKey("ÇAKIR"), AddressCatalogText.TurkishKey("CAKIR"));
    }

    [Theory]
    [InlineData("BARIŞ", "BARİŞ")]
    [InlineData("barış", "BARIS")]
    [InlineData("ÇAKIR", "CAKIR")]
    [InlineData("KÂHYA", "Kahya")]
    [InlineData("ŞEHİTLER", "SEHITLER")]
    public void MatchKey_IsEqual_ForNamesACaseAndAccentInsensitiveCollationCouldEquate(string left, string right) =>
        Assert.Equal(AddressCatalogText.MatchKey(left), AddressCatalogText.MatchKey(right));

    [Fact]
    public void MatchKey_KeepsDifferentNames_Apart()
    {
        Assert.NotEqual(AddressCatalogText.MatchKey("TEPE BAĞ1"), AddressCatalogText.MatchKey("TEPE BAĞ2"));
        Assert.NotEqual(AddressCatalogText.MatchKey("ATATÜRK"), AddressCatalogText.MatchKey("ATATÜRK MAHALLESİ"));
    }

    [Theory]
    [InlineData(0, "KARAAĞAÇ (Köy Sokağı)", "Köy Sokağı")]
    [InlineData(1, "CUMHURİYET (Meydan)", "Meydan")]
    [InlineData(2, "ATATÜRK (Bulvar)", "Bulvar")]
    [InlineData(3, "ALPARSLAN TÜRKEŞ (Cadde)", "Cadde")]
    [InlineData(4, "13705 (Sokak)", "Sokak")]
    [InlineData(5, "1498 KÜME EVLER (Küme Evler)", "Küme Evler")]
    public void StreetType_IsTheSourceLabel_WhenTheOfficialNameAgrees(int code, string official, string expected)
    {
        Assert.Null(AddressCatalogText.StreetTypeProblem(code, official, out var label));
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(4, "MODA (Cadde)", "street type code 4 (Sokak) does not match the official name")]
    [InlineData(9, "MODA (Sokak)", "unknown street type code 9")]
    [InlineData(null, "MODA (Sokak)", "no street type")]
    public void StreetType_IsRefused_WhenTheCodeIsUnknownOrDisagreesWithTheOfficialName(int? code, string official, string expected)
    {
        Assert.Equal(expected, AddressCatalogText.StreetTypeProblem(code, official, out var label));
        Assert.Null(label);
    }
}
