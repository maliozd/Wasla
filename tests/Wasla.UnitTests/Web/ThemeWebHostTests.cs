using System.Net;

namespace Wasla.UnitTests.Web;

/// <summary>
/// <see cref="ThemeWebHost"/> registers the tenant sign-in page's services by hand. When the tenant AuthController gained
/// a dependency (<c>ITenantSessionRegistry</c>, WAS-114) and the host did not, <c>/auth/login</c> answered an empty HTTP
/// 500 there. Only the headless-browser tests on this host noticed, as page-load timeouts, and those do not run on pull
/// requests into dev. This plain HTTP check runs in the fast scope too.
/// </summary>
public sealed class ThemeWebHostTests
{
    [Fact]
    public async Task AnonymousTenantSignInPage_IsServed_WithTheLoginForm()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ThemeWebHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = host.BaseAddress
        };

        using var response = await client.GetAsync("/auth/login", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        // The page carries an antiforgery token, so the HTML is never put in a failure message.
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"/auth/login: HTTP {(int)response.StatusCode}");
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(html.Contains("<form method=\"post\" action=\"/auth/login\">", StringComparison.Ordinal), "The login form is missing.");
        Assert.True(html.Contains("name=\"Email\"", StringComparison.Ordinal), "The email field is missing.");
        Assert.True(html.Contains("id=\"login-password\"", StringComparison.Ordinal), "The password field is missing.");
    }
}
