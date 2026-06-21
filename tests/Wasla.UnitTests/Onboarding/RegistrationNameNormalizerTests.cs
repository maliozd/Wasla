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

    [Fact]
    public void ExtractSlugFromHost_ReturnsSlugForTenantHost()
    {
        var result = RegistrationNameNormalizer.ExtractSlugFromHost(
            "https://HASAN-USTANIN-YERI.wasla.local:7200/signup/pending/123",
            "wasla.local");

        Assert.Equal("hasan-ustanin-yeri", result);
    }

    [Theory]
    [InlineData("wasla.local")]
    [InlineData("www.wasla.local")]
    [InlineData("not-wasla.example.com")]
    [InlineData("nested.sushim.wasla.local")]
    public void ExtractSlugFromHost_ReturnsNullForNonTenantOrInvalidHosts(string host)
    {
        var result = RegistrationNameNormalizer.ExtractSlugFromHost(host, "wasla.local");

        Assert.Null(result);
    }
}
