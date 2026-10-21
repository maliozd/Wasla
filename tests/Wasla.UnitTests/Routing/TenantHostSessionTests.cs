using System.Net;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Routing;

/// <summary>
/// WAS-66: sign-in on the nested tenant hosts through the real tenant resolution, cookie scheme and login form
/// (<see cref="TenantSessionWebHost"/>, tenants <c>alpha.app.wasla.local</c> and <c>beta.app.wasla.local</c>). The
/// session cookie stays host-only, so it reaches neither another tenant nor the application entry. All data is fake.
/// </summary>
public sealed class TenantHostSessionTests : IAsyncLifetime
{
    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantPostLoginRedirectTests.StartHostAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("alpha.app.wasla.local")]
    [InlineData("ALPHA.App.Wasla.Local")]
    [InlineData("alpha.app.wasla.local.")]
    public async Task EverySpellingOfTheTenantHost_SignsIntoThatTenant_WithAHostOnlyCookie(string host)
    {
        var owner = _host.SeedUser(_host.AlphaId, $"owner.{Guid.NewGuid():N}@alpha.test", UserRole.Owner);
        using var client = _host.Client(host);

        await client.LoginAsync(owner.Email);

        Assert.Contains(client.LastSetCookies, c => c.StartsWith(TenantAuthCookieNames.Active + "=", StringComparison.Ordinal));
        Assert.All(client.LastSetCookies, c => Assert.DoesNotContain("domain=", c, StringComparison.OrdinalIgnoreCase));
        var dashboard = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantHost_AndTheApplicationEntry_DoNotAcceptTheTenantsSession()
    {
        var owner = _host.SeedUser(_host.AlphaId, $"owner.{Guid.NewGuid():N}@alpha.test", UserRole.Owner);
        using var alpha = _host.Client(TenantSessionWebHost.AlphaHost);
        await alpha.LoginAsync(owner.Email);

        // A manual replay of the cookie, which a browser would not send to another host.
        using var beta = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        var onBeta = await beta.GetAsync("/dashboard");
        using var entry = alpha.ReplayTo(_host.BaseAddress, "app.wasla.local");
        var onEntry = await entry.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.Redirect, onBeta.StatusCode);
        Assert.StartsWith("http://beta.app.wasla.local/auth/login?", onBeta.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, onEntry.StatusCode);
        Assert.Equal("/tenant-address-required?returnUrl=%2Fdashboard", onEntry.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("admin.app.wasla.local")]
    [InlineData("alpha.app.wasla.local.evil.example")]
    [InlineData("alpha.wasla.local")]
    public async Task ReservedLookAlikeAndFormerHosts_NeverReachATenantsLoginForm(string host)
    {
        using var client = _host.Client(host);

        var response = await client.GetAsync("/auth/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/tenant-not-found?host={Uri.EscapeDataString(host)}", response.Headers.Location?.OriginalString);
    }
}
