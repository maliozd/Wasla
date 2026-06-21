using Wasla.Application.Onboarding;

namespace Wasla.UnitTests.Onboarding;

public sealed class RegistrationNameNormalizerTests
{
    [Fact]
    public void NormalizeSlug_TrimsAndLowercasesInput()
    {
        var result = RegistrationNameNormalizer.NormalizeSlug("  SushiM_42  ");

        Assert.Equal("sushim_42", result);
    }

    [Fact]
    public void NormalizeHostForComparison_RemovesSchemePortAndTrailingDot()
    {
        var result = RegistrationNameNormalizer.NormalizeHostForComparison(" HTTPS://SUSHIM.WASLA.LOCAL:7123/ ");

        Assert.Equal("sushim.wasla.local", result);
    }

    [Fact]
    public void GenerateSlugFromBusinessName_ConvertsTurkishCharactersAndCollapsesSeparators()
    {
        var result = RegistrationNameNormalizer.GenerateSlugFromBusinessName("  Şişli Çorba__Evi  ");

        Assert.Equal("sisli-corba-evi", result);
    }

    [Fact]
    public void BuildPrimaryDomain_TrimsLeadingDotFromMarketingDomain()
    {
        var result = RegistrationNameNormalizer.BuildPrimaryDomain("sushim", " .wasla.local ");

        Assert.Equal("sushim.wasla.local", result);
    }
}
