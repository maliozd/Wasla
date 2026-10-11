using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Web;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Rate limiting of the Web login forms (WAS-90): tenant <c>POST /auth/login</c> and Central Admin
/// <c>POST /admin/login</c>. Every test starts its own <see cref="TenantSessionWebHost"/> (real controllers, views,
/// antiforgery, tenant resolution and the production rate-limit registration) with its own limits. The client
/// connects from 127.0.0.1. Users and passwords are fake.
/// </summary>
public sealed class WebLoginRateLimitTests : IAsyncLifetime
{
    private const string AdminEmail = "ops@wasla.test";
    private const string AdminPassword = "Ops-Test-Password-42";
    private const string CentralHost = "wasla.local";

    private readonly CredentialCheckProbe _probe = new();
    private TenantSessionWebHost _web = null!;
    private AppUser _alphaOwner = null!;
    private AppUser _betaOwner = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_web is not null)
            await _web.DisposeAsync();
    }

    private async Task StartAsync(Dictionary<string, string?> configuration)
    {
        _web = await TenantSessionWebHost.StartAsync(configuration: configuration, configureServices: _probe.Register);
        _alphaOwner = _web.SeedUser(_web.AlphaId, "owner@alpha.test", UserRole.Owner);
        _betaOwner = _web.SeedUser(_web.BetaId, "owner@beta.test", UserRole.Owner);
        await using var central = _web.Central.CreateContext();
        central.CentralAdminUsers.Add(new CentralAdminUser
        {
            Email = AdminEmail,
            NormalizedEmail = AdminEmail.ToUpperInvariant(),
            DisplayName = "Ops",
            // Low work factor: fake test credentials only.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AdminPassword, workFactor: 4),
            IsActive = true
        });
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // --- Tenant login -------------------------------------------------------------------------------

    [Fact]
    public async Task TenantLogin_BelowTheLimitIsUnchanged_AndTheFirstAttemptOverItShowsTheLoginPageAgain_WithoutCredentialWork()
    {
        await StartAsync(RateLimitTestConfig.Limits(3));
        using var signedIn = _web.Client(TenantSessionWebHost.AlphaHost);
        var valid = await PostLoginAsync(signedIn, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password, "/orders/live");
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        Assert.Equal("/orders/live", valid.Headers.Location?.OriginalString);

        using var client = _web.Client(TenantSessionWebHost.AlphaHost);
        var invalidCredentials = Localized("en-US", "Auth.InvalidCredentials");
        foreach (var email in new[] { _alphaOwner.Email, "nobody@alpha.test" })
        {
            var invalid = await PostLoginAsync(client, "/auth/login", email, "Wrong-Password-1", "/orders/live");
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            Assert.Contains(invalidCredentials, await HtmlAsync(invalid), StringComparison.Ordinal);
        }

        Assert.Equal(3, _probe.TenantChecks);
        var token = await client.AntiforgeryTokenAsync("/auth/login?returnUrl=%2Forders%2Flive");
        var opened = _web.Tenants.Opened.Count;

        // Correct credentials do not get past the limit either.
        var limited = await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password, "/orders/live", token);

        Assert.Equal(3, _probe.TenantChecks);
        Assert.Equal(opened, _web.Tenants.Opened.Count);
        Assert.InRange(RateLimitTestConfig.RetryAfterSeconds(limited), 1, 300);
        Assert.Null(limited.Headers.Location);
        Assert.DoesNotContain(client.LastSetCookies, cookie => cookie.StartsWith(TenantAuthCookieNames.Active + "=", StringComparison.Ordinal));
        var html = await HtmlAsync(limited);
        Assert.Contains($"<div class=\"alert alert-warning\" role=\"alert\">{Localized("en-US", "Auth.Login.TooManyAttempts")}</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidCredentials, html, StringComparison.Ordinal);
        Assert.Matches(HiddenReturnUrl("/orders/live"), html);
        Assert.Contains($"value=\"{_alphaOwner.Email}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantSessionWebHost.Password, html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
    }

    public static TheoryData<string> Cultures => ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task LimitedLoginPages_AreLocalized_AndArabicIsRightToLeft(string culture)
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        var expected = Localized(culture, "Auth.Login.TooManyAttempts");
        var direction = culture == "ar-SA" ? "rtl" : "ltr";

        foreach (var (host, path, email) in new[]
                 {
                     (TenantSessionWebHost.AlphaHost, "/auth/login", _alphaOwner.Email),
                     (CentralHost, "/admin/login", AdminEmail)
                 })
        {
            using var client = _web.Client(host);
            client.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
                Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));
            await PostLoginAsync(client, path, email, "Wrong-Password-1");

            var limited = await PostLoginAsync(client, path, email, "Wrong-Password-1");

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            var html = await HtmlAsync(limited);
            Assert.Contains($"<html lang=\"{culture}\" dir=\"{direction}\">", html, StringComparison.Ordinal);
            Assert.Contains($"role=\"alert\">{expected}</div>", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Antiforgery_StillRunsFirst_AndARejectedTokenDoesNotUseAPermit()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var client = _web.Client(TenantSessionWebHost.AlphaHost);
        await client.AntiforgeryTokenAsync("/auth/login");

        var missingToken = await client.PostFormAsync("/auth/login",
            [new("Email", _alphaOwner.Email), new("Password", TenantSessionWebHost.Password)]);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var valid = await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password);
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);

        using var other = _web.Client(TenantSessionWebHost.AlphaHost);
        await other.AntiforgeryTokenAsync("/auth/login");
        var overLimitWithoutToken = await other.PostFormAsync("/auth/login",
            [new("Email", _alphaOwner.Email), new("Password", TenantSessionWebHost.Password)]);
        Assert.Equal(HttpStatusCode.BadRequest, overLimitWithoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(other, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
        Assert.Equal(1, _probe.TenantChecks);
    }

    [Fact]
    public async Task ConcurrentTenantLogins_NeverGetMorePermitsThanTheLimit()
    {
        const int permitLimit = 3;
        const int attempts = 15;
        await StartAsync(RateLimitTestConfig.Limits(permitLimit));
        var clients = Enumerable.Range(0, attempts).Select(_ => _web.Client(TenantSessionWebHost.AlphaHost)).ToList();
        try
        {
            var tokens = new List<string>();
            foreach (var client in clients)
                tokens.Add(await client.AntiforgeryTokenAsync("/auth/login"));

            // Permitted attempts stay inside the credential check until every other attempt has been answered.
            _probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = clients
                .Select((client, i) => PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", token: tokens[i]))
                .ToList();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (requests.Count(request => request.IsCompleted) < attempts - permitLimit)
                await Task.Delay(20, timeout.Token);

            Assert.All(requests.Where(request => request.IsCompleted), request => Assert.Equal(HttpStatusCode.TooManyRequests, request.Result.StatusCode));
            Assert.Equal(permitLimit, _probe.TenantChecks);

            _probe.Gate.SetResult();
            var responses = await Task.WhenAll(requests);
            Assert.Equal(permitLimit, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(attempts - permitLimit, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
            Assert.Equal(permitLimit, _probe.TenantChecks);
        }
        finally
        {
            _probe.Gate?.TrySetResult();
            clients.ForEach(client => client.Dispose());
        }
    }

    [Fact]
    public async Task EachTenant_HasItsOwnAllowance()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var alpha = _web.Client(TenantSessionWebHost.AlphaHost);
        using var beta = _web.Client(TenantSessionWebHost.BetaHost);

        await PostLoginAsync(alpha, "/auth/login", _alphaOwner.Email, "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(alpha, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(beta, "/auth/login", _betaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_SeparatesClientsOnlyThroughATrustedProxy()
    {
        await StartAsync(RateLimitTestConfig.Limits(1).TrustingLoopbackProxy());
        using var client = _web.Client(TenantSessionWebHost.AlphaHost);

        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", forwardedFor: "198.51.100.20")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", forwardedFor: "198.51.100.20")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", forwardedFor: "203.0.113.9, 198.51.100.20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", forwardedFor: "198.51.100.21")).StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_WithoutATrustedProxy_DoesNotChangeThePartition()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var client = _web.Client(TenantSessionWebHost.AlphaHost);

        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", forwardedFor: "198.51.100.20")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, "Wrong-Password-1", forwardedFor: "198.51.100.21")).StatusCode);
        Assert.Equal(1, _probe.TenantChecks);
    }

    [Fact]
    public async Task UnknownTenantHost_IsStillRedirected_AndNeverLimited()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var unknown = _web.Client("unknown.wasla.local");

        for (var i = 0; i < 3; i++)
        {
            var response = await unknown.PostFormAsync("/auth/login", [new("Email", _alphaOwner.Email), new("Password", "Wrong-Password-1")]);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/tenant-not-found?host=unknown.wasla.local", response.Headers.Location?.OriginalString);
        }

        Assert.Equal(0, _probe.TenantChecks);
        Assert.Empty(_web.Tenants.Opened);
    }

    [Fact]
    public async Task TenantDatabaseUnavailable_BelowTheLimit_StillFailsWith500()
    {
        await StartAsync(RateLimitTestConfig.Limits(2));
        _web.Tenants.Override(_web.AlphaId, _ => throw new SimulatedDbException());
        using var client = _web.Client(TenantSessionWebHost.AlphaHost);

        var failed = await PostLoginAsync(client, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(1, _probe.TenantChecks);
    }

    // --- Central Admin login ------------------------------------------------------------------------

    [Fact]
    public async Task AdminLogin_IsLimitedPerClient_KeepsTheReturnUrl_AndDoesNotStarveTenantLogins()
    {
        await StartAsync(RateLimitTestConfig.Limits(2));
        using var admin = _web.Client(CentralHost);
        var invalidAdmin = Localized("en-US", "Admin.InvalidCredentials");

        var wrongPassword = await PostLoginAsync(admin, "/admin/login", AdminEmail, "Wrong-Password-1", "/admin/customers");
        var unknownAdmin = await PostLoginAsync(admin, "/admin/login", "nobody@wasla.test", "Wrong-Password-1", "/admin/customers");
        Assert.Contains(invalidAdmin, await HtmlAsync(wrongPassword), StringComparison.Ordinal);
        Assert.Contains(invalidAdmin, await HtmlAsync(unknownAdmin), StringComparison.Ordinal);
        Assert.Equal(2, _probe.AdminChecks);

        // The same client on another host shares the admin allowance: admin partitions are per client only.
        using var adminOnTenantHost = _web.Client(TenantSessionWebHost.AlphaHost);
        var limited = await PostLoginAsync(adminOnTenantHost, "/admin/login", AdminEmail, AdminPassword, "/admin/customers");

        Assert.Equal(2, _probe.AdminChecks);
        Assert.InRange(RateLimitTestConfig.RetryAfterSeconds(limited), 1, 300);
        Assert.DoesNotContain(adminOnTenantHost.LastSetCookies, cookie => cookie.StartsWith(CentralAdminAuthCookieNames.Active + "=", StringComparison.Ordinal));
        var html = await HtmlAsync(limited);
        Assert.Contains($"role=\"alert\">{Localized("en-US", "Auth.Login.TooManyAttempts")}</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidAdmin, html, StringComparison.Ordinal);
        Assert.Matches(HiddenReturnUrl("/admin/customers"), html);
        Assert.DoesNotContain(AdminPassword, html, StringComparison.Ordinal);

        // Other endpoints keep working: the login pages, the forgot-password page and a tenant login.
        using var tenant = _web.Client(TenantSessionWebHost.AlphaHost);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync("/auth/forgot-password")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(tenant, "/auth/login", _alphaOwner.Email, TenantSessionWebHost.Password)).StatusCode);
    }

    [Fact]
    public async Task ExhaustedTenantLogin_DoesNotLimitAdminLogin()
    {
        await StartAsync(RateLimitTestConfig.Limits(1));
        using var tenant = _web.Client(TenantSessionWebHost.AlphaHost);
        await PostLoginAsync(tenant, "/auth/login", _alphaOwner.Email, "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(tenant, "/auth/login", _alphaOwner.Email, "Wrong-Password-1")).StatusCode);

        using var admin = _web.Client(CentralHost);
        var response = await PostLoginAsync(admin, "/admin/login", AdminEmail, AdminPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.OriginalString);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostLoginAsync(
        TenantSessionClient client,
        string path,
        string email,
        string password,
        string? returnUrl = null,
        string? token = null,
        string? forwardedFor = null)
    {
        token ??= await client.AntiforgeryTokenAsync(returnUrl is null ? path : $"{path}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var form = new List<KeyValuePair<string, string>>
        {
            new("Email", email),
            new("Password", password),
            new("RememberMe", "false"),
            new("__RequestVerificationToken", token)
        };
        if (returnUrl is not null)
            form.Add(new("ReturnUrl", returnUrl));

        KeyValuePair<string, string>[] headers = forwardedFor is null ? [] : [RateLimitTestConfig.ForwardedFor(forwardedFor)];
        return await client.SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form), headers);
    }

    private static async Task<string> HtmlAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private string Localized(string culture, string key)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var value = _web.Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key];
            Assert.False(value.ResourceNotFound, $"{key} has no {culture} resource.");
            return value.Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static Regex HiddenReturnUrl(string returnUrl) =>
        new($"<input type=\"hidden\"[^>]*name=\"ReturnUrl\"[^>]*value=\"{Regex.Escape(returnUrl)}\"");

    private sealed class SimulatedDbException() : DbException("Simulated tenant database outage (test).");
}
