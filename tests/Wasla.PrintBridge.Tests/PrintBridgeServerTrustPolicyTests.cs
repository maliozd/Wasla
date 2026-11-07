using Wasla.PrintBridge.Setup;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-91: which servers a setup link may name. Production is HTTPS on the Wasla Orders hosts only, classified as the
/// server classifies them; the local development hosts and plain HTTP only under the development policy, which only
/// Debug builds use. The earlier allowlist (<c>wasla.com</c>, <c>wasla.app</c>, and development hosts in every build)
/// is gone.
/// </summary>
public sealed class PrintBridgeServerTrustPolicyTests
{
    [Theory]
    [InlineData("https://sushim.app.wasla.tr/")]
    [InlineData("https://sushim.app.wasla.tr")]
    [InlineData("https://SUSHIM.App.Wasla.TR/")]
    [InlineData("https://sushim.app.wasla.tr:443/")]
    [InlineData("https://sushim.app.wasla.tr./")]
    [InlineData("https://app.wasla.tr/")]
    [InlineData("https://wasla.tr/")]
    [InlineData("https://www.wasla.tr/")]
    public void Production_TrustsHttpsWaslaHosts(string serverUrl)
    {
        Assert.True(PrintBridgeServerTrustPolicy.Production.IsTrusted(serverUrl));
        Assert.True(PrintBridgeServerTrustPolicy.Development.IsTrusted(serverUrl));
    }

    [Theory]
    [InlineData("http://sushim.app.wasla.tr/")] // plain HTTP
    [InlineData("https://panel.wasla.com/")] // not a Wasla domain (the former allowlist)
    [InlineData("https://sushim.wasla.app/")]
    [InlineData("https://sushim.wasla.tr/")] // the former tenant shape
    [InlineData("https://a.b.app.wasla.tr/")] // an extra label
    [InlineData("https://admin.app.wasla.tr/")] // a reserved name
    [InlineData("https://sushim.app.wasla.tr.evil.example/")] // a look-alike suffix
    [InlineData("https://evilapp.wasla.tr/")]
    [InlineData("https://user:pass@sushim.app.wasla.tr/")] // credentials in the address
    [InlineData("ftp://sushim.app.wasla.tr/")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Production_RefusesEverythingElse(string serverUrl)
    {
        Assert.False(PrintBridgeServerTrustPolicy.Production.IsTrusted(serverUrl));
    }

    [Theory]
    [InlineData("https://localhost:7200/")]
    [InlineData("http://localhost:5200/")]
    [InlineData("https://127.0.0.1:7200/")]
    [InlineData("https://[::1]:7200/")]
    [InlineData("https://wasla.local:7200/")]
    [InlineData("http://sushim.app.wasla.local:5200/")]
    [InlineData("https://sushim.wasla.local:7200/")]
    public void DevelopmentHosts_AreTrustedOnlyByTheDevelopmentPolicy(string serverUrl)
    {
        Assert.True(PrintBridgeServerTrustPolicy.Development.IsTrusted(serverUrl));
        Assert.False(PrintBridgeServerTrustPolicy.Production.IsTrusted(serverUrl));
    }

    [Theory]
    [InlineData("https://evilwasla.local/")]
    [InlineData("https://wasla.local.evil.example/")]
    [InlineData("https://192.168.1.10/")]
    [InlineData("http://print.example.com/")]
    public void Development_StillRefusesOtherHosts(string serverUrl)
    {
        Assert.False(PrintBridgeServerTrustPolicy.Development.IsTrusted(serverUrl));
    }

    [Fact]
    public void TheBuildDecides_DevelopmentHostsAreNeverTrustedByARelease()
    {
#if DEBUG
        Assert.Same(PrintBridgeServerTrustPolicy.Development, PrintBridgeServerTrustPolicy.ForCurrentBuild());
#else
        Assert.Same(PrintBridgeServerTrustPolicy.Production, PrintBridgeServerTrustPolicy.ForCurrentBuild());
        Assert.False(PrintBridgeServerTrustPolicy.ForCurrentBuild().AllowsDevelopmentServers);
#endif
    }

    [Theory]
    [InlineData("https://bahce.app.wasla.tr/", "https://bahce.app.wasla.tr", true)]
    [InlineData("https://bahce.app.wasla.tr/", "https://BAHCE.app.wasla.tr:443/some/path", true)]
    [InlineData("https://bahce.app.wasla.tr/", "http://bahce.app.wasla.tr/", false)]
    [InlineData("https://bahce.app.wasla.tr/", "https://bahce.app.wasla.tr:8443/", false)]
    [InlineData("https://bahce.app.wasla.tr/", "https://anadolu.app.wasla.tr/", false)]
    [InlineData("https://bahce.app.wasla.tr/", "https://bahce.app.wasla.tr.evil.example/", false)]
    [InlineData("https://bahce.app.wasla.tr/", null, false)]
    public void SameOrigin_ComparesSchemeHostAndPortOnly(string first, string? second, bool expected)
    {
        Assert.Equal(expected, PrintBridgeServerTrustPolicy.IsSameOrigin(first, second));
    }

    [Theory]
    [InlineData("https://bahce.app.wasla.tr/", "bahce.app.wasla.tr")]
    [InlineData("https://Bahce.App.Wasla.TR:443/x", "bahce.app.wasla.tr")]
    [InlineData("https://localhost:7200", "localhost:7200")]
    [InlineData("https://[::1]:7200/", "[::1]:7200")]
    [InlineData("nonsense", "")]
    public void DisplayHost_ShowsTheHostAndAnUnusualPort_Only(string serverUrl, string expected)
    {
        Assert.Equal(expected, PrintBridgeServerTrustPolicy.DisplayHost(serverUrl));
    }
}
