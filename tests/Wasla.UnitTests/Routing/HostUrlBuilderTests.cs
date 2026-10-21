using Microsoft.AspNetCore.Http;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Routing;

/// <summary>
/// WAS-66: generated marketing and application URLs take their host from configuration. Outside Development a request
/// on <c>localhost</c> or an IP address (a direct or hostile request) never decides the host or the port of a canonical
/// URL, the application sign-in link, the signup link or the homepage link; only Development keeps a local run local.
/// </summary>
public sealed class HostUrlBuilderTests
{
    private const string Marketing = "wasla.tr";
    private const string Application = "app.wasla.tr";

    public static TheoryData<string, string> LocalRequestsOutsideDevelopment()
    {
        var data = new TheoryData<string, string>();
        foreach (var environment in new[] { "Production", "Staging", "Pilot" })
        {
            foreach (var host in new[] { "localhost", "127.0.0.1", "[::1]", "10.0.0.5" })
                data.Add(environment, host);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LocalRequestsOutsideDevelopment))]
    public void OutsideDevelopment_ALocalRequestHost_NeverReachesAGeneratedUrl(string environment, string host)
    {
        var env = new TestHostEnvironment(environment);
        var request = Request("https", host, 8443);
        // Staging keeps the request port (Development and Staging); the host is still the configured one.
        var port = environment == "Staging" ? ":8443" : string.Empty;

        Assert.Equal($"https://wasla.tr{port}/pos", PublicSiteUrls.BuildCanonicalUrl(request, env, Marketing, "/pos"));
        Assert.Equal($"https://app.wasla.tr{port}/auth/login", TenantWelcomeUrlBuilder.BuildApplicationLoginUrl(request, env, Application));
        Assert.Equal($"https://wasla.tr{port}/signup", TenantWelcomeUrlBuilder.BuildCentralSignupUrl(request, env, Marketing));
        Assert.Equal($"https://wasla.tr{port}/", TenantWelcomeUrlBuilder.BuildCentralHomepageUrl(request, env, Marketing));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Pilot")]
    public void OutsideDevelopment_TheRequestPortIsDropped(string environment)
    {
        var env = new TestHostEnvironment(environment);
        var request = Request("https", "www.wasla.tr", 8443);

        Assert.Equal("https://wasla.tr/", PublicSiteUrls.BuildCanonicalUrl(request, env, Marketing, "/"));
        Assert.Equal("https://app.wasla.tr/auth/login", TenantWelcomeUrlBuilder.BuildApplicationLoginUrl(request, env, Application));
    }

    [Fact]
    public void InDevelopment_LocalhostKeepsTheLocalRunLocal()
    {
        var env = new TestHostEnvironment("Development");
        var request = Request("http", "localhost", 5200);

        Assert.Equal("http://localhost:5200/", PublicSiteUrls.BuildCanonicalUrl(request, env, "wasla.local", "/"));
        Assert.Equal("http://localhost:5200/auth/login", TenantWelcomeUrlBuilder.BuildApplicationLoginUrl(request, env, "app.wasla.local"));
        Assert.Equal("http://localhost:5200/signup", TenantWelcomeUrlBuilder.BuildCentralSignupUrl(request, env, "wasla.local"));
        Assert.Equal("http://localhost:5200/", TenantWelcomeUrlBuilder.BuildCentralHomepageUrl(request, env, "wasla.local"));
    }

    [Fact]
    public void InDevelopment_AnIpAddressKeepsItsCanonicalHost_AndLinksUseTheConfiguredDomains()
    {
        var env = new TestHostEnvironment("Development");
        var request = Request("http", "127.0.0.1", 5200);

        Assert.Equal("http://127.0.0.1:5200/pos", PublicSiteUrls.BuildCanonicalUrl(request, env, "wasla.local", "/pos"));
        Assert.Equal("http://app.wasla.local:5200/auth/login", TenantWelcomeUrlBuilder.BuildApplicationLoginUrl(request, env, "app.wasla.local"));
    }

    [Fact]
    public void InDevelopment_TheDevelopmentHostsUseTheConfiguredDomains_WithThePort()
    {
        var env = new TestHostEnvironment("Development");
        var request = Request("http", "sushim.app.wasla.local", 5200);

        Assert.Equal("http://wasla.local:5200/", TenantWelcomeUrlBuilder.BuildCentralHomepageUrl(request, env, "wasla.local"));
        Assert.Equal("http://app.wasla.local:5200/auth/login", TenantWelcomeUrlBuilder.BuildApplicationLoginUrl(request, env, "app.wasla.local"));
    }

    private static HttpRequest Request(string scheme, string host, int port)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = scheme;
        request.Host = new HostString(host, port);
        return request;
    }
}
