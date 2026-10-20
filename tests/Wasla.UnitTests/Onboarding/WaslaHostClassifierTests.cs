using Wasla.Application.Onboarding;

namespace Wasla.UnitTests.Onboarding;

/// <summary>
/// WAS-66: the host contract with the production domains. <c>wasla.tr</c> is the marketing site, <c>app.wasla.tr</c> the
/// application entry, and <c>{slug}.app.wasla.tr</c> exactly one tenant; everything else fails closed.
/// </summary>
public sealed class WaslaHostClassifierTests
{
    private const string Marketing = "wasla.tr";
    private const string Application = "app.wasla.tr";

    [Theory]
    [InlineData("wasla.tr", "wasla.tr")]
    [InlineData("WWW.Wasla.TR", "www.wasla.tr")]
    [InlineData("wasla.tr.", "wasla.tr")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "[::1]")]
    public void MarketingHostsLocalhostAndAddresses_ArePublic(string host, string normalized)
    {
        var result = WaslaHostClassifier.Classify(host, Marketing, Application);

        Assert.Equal(WaslaHostKind.Public, result.Kind);
        Assert.Equal(normalized, result.Host);
        Assert.Null(result.Slug);
    }

    [Theory]
    [InlineData("app.wasla.tr")]
    [InlineData("APP.Wasla.Tr")]
    [InlineData(" app.wasla.tr. ")]
    public void TheApplicationDomain_IsTheApplicationEntry_NotATenant(string host)
    {
        var result = WaslaHostClassifier.Classify(host, Marketing, Application);

        Assert.Equal(WaslaHostKind.Application, result.Kind);
        Assert.Equal("app.wasla.tr", result.Host);
        Assert.Null(result.Slug);
    }

    [Theory]
    [InlineData("sushim.app.wasla.tr")]
    [InlineData("SUSHIM.App.Wasla.TR")]
    [InlineData("sushim.app.wasla.tr.")]
    public void OneSlugLabelUnderTheApplicationDomain_IsThatTenant_InOneNormalizedForm(string host)
    {
        var result = WaslaHostClassifier.Classify(host, Marketing, Application);

        Assert.Equal(WaslaHostKind.Tenant, result.Kind);
        Assert.Equal("sushim.app.wasla.tr", result.Host);
        Assert.Equal("sushim", result.Slug);
    }

    [Theory]
    [InlineData("hasan-ustanin-yeri.app.wasla.tr", "hasan-ustanin-yeri")]
    [InlineData("pide_42.app.wasla.tr", "pide_42")]
    [InlineData("7dunya.app.wasla.tr", "7dunya")]
    public void ValidSlugFormats_AreTenants(string host, string slug)
    {
        var result = WaslaHostClassifier.Classify(host, Marketing, Application);

        Assert.Equal(WaslaHostKind.Tenant, result.Kind);
        Assert.Equal(slug, result.Slug);
    }

    [Theory]
    // Empty labels and additional labels.
    [InlineData(".app.wasla.tr")]
    [InlineData("sushim..app.wasla.tr")]
    [InlineData("sushim.app.wasla.tr..")]
    [InlineData("a.sushim.app.wasla.tr")]
    // Invalid slugs.
    [InlineData("-sushim.app.wasla.tr")]
    [InlineData("_sushim.app.wasla.tr")]
    [InlineData("sus!him.app.wasla.tr")]
    [InlineData("şişli.app.wasla.tr")]
    // Reserved application and infrastructure labels.
    [InlineData("app.app.wasla.tr")]
    [InlineData("www.app.wasla.tr")]
    [InlineData("admin.app.wasla.tr")]
    [InlineData("api.app.wasla.tr")]
    [InlineData("docs.app.wasla.tr")]
    [InlineData("POS.app.wasla.tr")]
    // Suffix confusion: names that contain or end in a Wasla domain without its exact boundary.
    [InlineData("sushim.app.wasla.tr.evil.example")]
    [InlineData("app.wasla.tr.evil.example")]
    [InlineData("wasla.tr.evil.example")]
    [InlineData("evilapp.wasla.tr")]
    [InlineData("sushimapp.wasla.tr")]
    [InlineData("app-wasla.tr")]
    // Root-level names are not tenants: the former {slug}.wasla.tr shape and future product hosts.
    [InlineData("sushim.wasla.tr")]
    [InlineData("admin.wasla.tr")]
    [InlineData("api.wasla.tr")]
    // Arbitrary hosts.
    [InlineData("evil.example")]
    [InlineData("intranet")]
    public void EveryOtherHost_IsInvalid(string host)
    {
        var result = WaslaHostClassifier.Classify(host, Marketing, Application);

        Assert.Equal(WaslaHostKind.Invalid, result.Kind);
        Assert.Null(result.Slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    public void AnEmptyHost_IsInvalid(string? host)
    {
        Assert.Equal(WaslaHostKind.Invalid, WaslaHostClassifier.Classify(host, Marketing, Application).Kind);
    }

    [Fact]
    public void ASlugLongerThanOneDnsLabel_IsInvalid()
    {
        var longest = new string('a', RegistrationNameNormalizer.MaxSlugLength);

        Assert.Equal(WaslaHostKind.Tenant, WaslaHostClassifier.Classify($"{longest}.app.wasla.tr", Marketing, Application).Kind);
        Assert.Equal(WaslaHostKind.Invalid, WaslaHostClassifier.Classify($"{longest}a.app.wasla.tr", Marketing, Application).Kind);
    }

    [Fact]
    public void TheDevelopmentDomains_FollowTheSameShape()
    {
        Assert.Equal(WaslaHostKind.Public, WaslaHostClassifier.Classify("wasla.local", "wasla.local", "app.wasla.local").Kind);
        Assert.Equal(WaslaHostKind.Public, WaslaHostClassifier.Classify("www.wasla.local", "wasla.local", "app.wasla.local").Kind);
        Assert.Equal(WaslaHostKind.Application, WaslaHostClassifier.Classify("app.wasla.local", "wasla.local", "app.wasla.local").Kind);
        Assert.Equal("sushim", WaslaHostClassifier.Classify("sushim.app.wasla.local", "wasla.local", "app.wasla.local").Slug);
        Assert.Equal(WaslaHostKind.Invalid, WaslaHostClassifier.Classify("sushim.wasla.local", "wasla.local", "app.wasla.local").Kind);
    }

    [Fact]
    public void ConfiguredDomains_AreComparedTrimmedAndCaseInsensitively()
    {
        var result = WaslaHostClassifier.Classify("sushim.app.wasla.tr", " .WASLA.TR. ", " APP.Wasla.tr. ");

        Assert.Equal(WaslaHostKind.Tenant, result.Kind);
        Assert.Equal("sushim", result.Slug);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("www")]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("docs")]
    public void TheApplicationAndInfrastructureNames_AreReservedSlugs(string slug)
    {
        Assert.True(RegistrationNameNormalizer.IsReservedSlug(slug));
    }
}
