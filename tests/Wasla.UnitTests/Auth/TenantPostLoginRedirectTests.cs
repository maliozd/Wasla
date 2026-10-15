using System.Net;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Web;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Where a tenant user lands after signing in, and the Access denied page (WAS-113). Every test drives the tenant
/// application over HTTP: real login form, real cookie scheme with session revalidation, tenant resolved from the Host
/// header, the Program.cs role policies and the real controllers and views over SQLite tenant databases. Users and
/// passwords are fake.
/// </summary>
public sealed class TenantPostLoginRedirectTests : IAsyncLifetime
{
    private const string LiveScreen = "/orders/live-display";
    private const string Dashboard = "/dashboard";

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await StartHostAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<UserRole> Roles =>
    [
        UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer
    ];

    // --- Landing page per role ------------------------------------------------------------------------

    /// <summary>
    /// The reported failure: a Kitchen user was always sent to <c>/dashboard</c> (<c>CanViewReports</c>), which Kitchen
    /// may not open, so the first page after every valid login was Access denied.
    /// </summary>
    [Fact]
    public async Task KitchenLogin_LandsOnAPageKitchenMayOpen_NotOnAccessDenied()
    {
        var kitchen = Seed(UserRole.Kitchen);
        using var client = Alpha();

        var landing = await PostLoginAsync(client, kitchen.Email);
        var page = await client.GetAsync(landing);

        Assert.Equal(LiveScreen, landing);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    /// <summary>
    /// The whole chain a browser follows after the login POST ends on a page the role may open: Owner, Manager and
    /// Viewer (<c>CanViewReports</c>) on the Dashboard, Kitchen and Cashier on the Live Screen.
    /// </summary>
    [Theory]
    [MemberData(nameof(Roles))]
    public async Task EveryRole_LoginRedirectChain_EndsOnTheRolesAuthorizedLandingPage(UserRole role)
    {
        var user = Seed(role);
        using var client = Alpha();

        var (finalPath, finalStatus, chain) = await FollowAsync(client, await PostLoginAsync(client, user.Email));

        Assert.True(finalStatus == HttpStatusCode.OK, $"{role}: {string.Join(" -> ", chain)} ended with {(int)finalStatus}");
        Assert.DoesNotContain(chain, step => step.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ExpectedLanding(role), finalPath);
    }

    /// <summary>
    /// Negative control: with the original unconditional <c>/dashboard</c> redirect put back (a resolver that always
    /// answers <c>/dashboard</c>), the Kitchen chain ends on Access denied, which the role tests above refuse.
    /// </summary>
    [Fact]
    public async Task NegativeControl_TheOriginalUnconditionalDashboardRedirect_SendsKitchenToAccessDenied()
    {
        await using var original = await StartHostAsync(services =>
        {
            services.RemoveAll<ITenantLandingPageResolver>();
            services.AddScoped<ITenantLandingPageResolver, DashboardLandingPages>();
        });
        var kitchen = original.SeedUser(original.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        using var client = original.Client(TenantSessionWebHost.AlphaHost);

        var (_, _, chain) = await FollowAsync(client, await PostLoginAsync(client, kitchen.Email));

        Assert.Equal([Dashboard, "/auth/access-denied?ReturnUrl=%2Fdashboard"], chain);
    }

    /// <summary>
    /// The signup welcome link uses the same landing page, from the user's current role: the new Owner lands on the
    /// Dashboard (with the setup panel, not the static onboarding cards), and a user whose role changed since the link
    /// was issued lands on the page of the current role.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Owner, Dashboard)]
    [InlineData(UserRole.Kitchen, LiveScreen)]
    public async Task WelcomeLink_LandsOnTheCurrentRolesLandingPage(UserRole currentRole, string expected)
    {
        var owner = Seed(UserRole.Owner);
        using var scope = _host.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<Wasla.Application.Abstractions.Auth.ISignupCompletionTokenService>();
        var link = tokens.CreateToken(new Wasla.Application.Abstractions.Auth.SignupCompletionPayload(
            _host.AlphaId, owner.Id, owner.Email, owner.FullName, UserRole.Owner));
        if (currentRole != UserRole.Owner)
            _host.UpdateUser(_host.AlphaId, owner.Id, user => user.Role = currentRole);
        using var client = Alpha();

        var response = await client.GetAsync($"/auth/welcome?token={Uri.EscapeDataString(link)}");
        var landing = await client.GetAsync(Location(response));

        Assert.Equal(expected, Location(response));
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
    }

    /// <summary>
    /// Obsolete Staff is in no policy and its session is rejected on the next request: it has no landing page, goes to
    /// the login page, and that page renders instead of redirecting again.
    /// </summary>
    [Fact]
    public async Task ObsoleteStaff_HasNoLandingPage_AndTheLoginPageDoesNotLoop()
    {
#pragma warning disable CS0618 // Obsolete Staff: still a stored role value.
        var staff = Seed(UserRole.Staff);
#pragma warning restore CS0618
        using var client = Alpha();

        var location = await PostLoginAsync(client, staff.Email);
        var (finalPath, finalStatus, _) = await FollowAsync(client, location);

        Assert.Equal("/auth/login", location);
        Assert.Equal("/auth/login", finalPath);
        Assert.Equal(HttpStatusCode.OK, finalStatus);
        Assert.Null(client.AuthCookie);
    }

    // --- ReturnUrl ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Owner, "/settings/users")]
    [InlineData(UserRole.Owner, "/orders?status=New&page=2")]
    [InlineData(UserRole.Manager, "/orders/order-settings")]
    [InlineData(UserRole.Kitchen, "/orders?status=New")]
    [InlineData(UserRole.Kitchen, "/help")]
    [InlineData(UserRole.Cashier, "/orders")]
    [InlineData(UserRole.Viewer, "/orders/live-display")]
    public async Task SafeLocalReturnUrl_TheUserMayOpen_IsFollowedWithItsQuery(UserRole role, string returnUrl)
    {
        var user = Seed(role);
        using var client = Alpha();

        var location = await PostLoginAsync(client, user.Email, returnUrl);
        var page = await client.GetAsync(location);

        Assert.Equal(returnUrl, location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    /// <summary>A local path is not enough: one the role may not open is replaced by the role's landing page.</summary>
    [Theory]
    [InlineData(UserRole.Kitchen, "/dashboard")]
    [InlineData(UserRole.Kitchen, "/settings/users")]
    [InlineData(UserRole.Kitchen, "/orders/order-settings")]
    [InlineData(UserRole.Cashier, "/dashboard")]
    [InlineData(UserRole.Cashier, "/platform-connections")]
    [InlineData(UserRole.Manager, "/settings/users")]
    [InlineData(UserRole.Viewer, "/orders/order-settings")]
    public async Task LocalReturnUrl_TheUserMayNotOpen_IsReplacedByTheLandingPage(UserRole role, string returnUrl)
    {
        var user = Seed(role);
        using var client = Alpha();

        var (finalPath, finalStatus, chain) = await FollowAsync(client, await PostLoginAsync(client, user.Email, returnUrl));

        Assert.Equal(ExpectedLanding(role), chain[0]);
        Assert.Equal(ExpectedLanding(role), finalPath);
        Assert.Equal(HttpStatusCode.OK, finalStatus);
    }

    /// <summary>
    /// Other hosts and anything that could become one when decoded, malformed values, POST-only and unknown paths and
    /// anonymous pages are never followed; the user lands on their own page on this host. One login per value.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Owner)]
    public async Task UnsafeOrUnusableReturnUrls_AreReplacedByTheLandingPage(UserRole role)
    {
        var user = Seed(role);
        string[] hostile =
        [
            // Other hosts and schemes
            "https://evil.example/dashboard", "http://evil.example", "HtTpS://evil.example", "https:/evil.example",
            "javascript:alert(1)", "data:text/html,x", "http://beta.wasla.local/dashboard",
            // Protocol-relative and backslash forms
            "//evil.example/dashboard", "///evil.example", "/\\evil.example", "\\\\evil.example", "\\/evil.example",
            "/\\/evil.example",
            // Encoded and double-encoded
            "%2F%2Fevil.example", "/%2F%2Fevil.example", "/%2fevil.example", "/%5Cevil.example", "/%252F%252Fevil.example",
            "%252F%252Fevil.example", "/orders/%2e%2e/settings/users", "/orders/../settings/users", "/./dashboard",
            // Malformed
            "", " ", " /orders", "/orders%", "/orders%zz", "/orders\r\nSet-Cookie: x=1", "/orders\t", "/ord ers",
            "/orders#fragment", "~/orders", "orders", "?x=1", "/orders/ç",
            // Not a page: POST-only, unknown, anonymous, Central Admin
            $"/orders/{Guid.NewGuid()}/approve", "/no-such-page", "/", "/tenant-address-required", "/admin",
            "/health/live", "/hubs/tenant-events"
        ];

        var failures = new List<string>();
        foreach (var returnUrl in hostile)
        {
            using var client = Alpha();
            var location = await PostLoginAsync(client, user.Email, returnUrl);
            if (location != ExpectedLanding(role))
                failures.Add($"{Printable(returnUrl)} -> {location}");
        }

        Assert.Empty(failures);
    }

    /// <summary>The login, logout and Access denied pages are never a destination, in any spelling.</summary>
    [Fact]
    public async Task AuthPagesAsReturnUrl_NeverLoop()
    {
        var kitchen = Seed(UserRole.Kitchen);
        string[] loops =
        [
            "/auth/login", "/AUTH/Login", "/auth/login?ReturnUrl=%2Fauth%2Flogin", "/auth/access-denied",
            "/auth/access-denied?ReturnUrl=%2Fdashboard", "/auth/logout", "/auth", "/auth/", "/auth/welcome",
            "/%61uth/login"
        ];

        foreach (var returnUrl in loops)
        {
            using var client = Alpha();
            var (finalPath, finalStatus, chain) = await FollowAsync(client, await PostLoginAsync(client, kitchen.Email, returnUrl));

            Assert.True(LiveScreen == finalPath && finalStatus == HttpStatusCode.OK,
                $"{returnUrl}: {string.Join(" -> ", chain)} ({(int)finalStatus})");
        }
    }

    /// <summary>An already signed-in user who opens the login page goes to a page they may open, never back to login.</summary>
    [Fact]
    public async Task SignedInUser_OpeningTheLoginPage_GoesToAnAuthorizedPage()
    {
        var kitchen = Seed(UserRole.Kitchen);
        using var client = Alpha();
        await PostLoginAsync(client, kitchen.Email);

        var plain = await client.GetAsync("/auth/login");
        var authorized = await client.GetAsync("/auth/login?returnUrl=%2Forders%3Fstatus%3DNew");
        var unauthorized = await client.GetAsync("/auth/login?returnUrl=%2Fdashboard");
        var external = await client.GetAsync("/auth/login?returnUrl=%2F%2Fevil.example");
        var loop = await client.GetAsync("/auth/login?returnUrl=%2Fauth%2Flogin");

        Assert.Equal(LiveScreen, Location(plain));
        Assert.Equal("/orders?status=New", Location(authorized));
        Assert.Equal(LiveScreen, Location(unauthorized));
        Assert.Equal(LiveScreen, Location(external));
        Assert.Equal(LiveScreen, Location(loop));
    }

    // --- Direct navigation ----------------------------------------------------------------------------

    /// <summary>
    /// Authorization of the pages is unchanged: an anonymous request is sent to login with its ReturnUrl, and the login
    /// then follows it only when the role may open it. A signed-in role without the policy still gets Access denied.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Owner, Dashboard, Dashboard)]
    [InlineData(UserRole.Kitchen, Dashboard, LiveScreen)]
    [InlineData(UserRole.Kitchen, "/help", "/help")]
    [InlineData(UserRole.Cashier, "/settings/users", LiveScreen)]
    public async Task AnonymousDirectNavigation_ThenLogin_FollowsOnlyAnAuthorizedReturnUrl(UserRole role, string protectedPath, string expected)
    {
        var user = Seed(role);
        using var client = Alpha();

        var challenge = await client.GetAsync(protectedPath);
        var loginUrl = Location(challenge);
        Assert.StartsWith("/auth/login?ReturnUrl=", loginUrl, StringComparison.Ordinal);
        var returnUrl = Uri.UnescapeDataString(loginUrl["/auth/login?ReturnUrl=".Length..]);

        Assert.Equal(protectedPath, returnUrl);
        Assert.Equal(expected, await PostLoginAsync(client, user.Email, returnUrl));
    }

    [Theory]
    [InlineData(UserRole.Kitchen, Dashboard)]
    [InlineData(UserRole.Kitchen, "/settings/users")]
    [InlineData(UserRole.Cashier, Dashboard)]
    [InlineData(UserRole.Viewer, "/settings/users")]
    public async Task SignedInDirectNavigation_ToAPageTheRoleMayNotOpen_StillGetsAccessDenied(UserRole role, string path)
    {
        var user = Seed(role);
        using var client = Alpha();
        await PostLoginAsync(client, user.Email);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/access-denied", Location(response), StringComparison.Ordinal);
    }

    // --- Access denied --------------------------------------------------------------------------------

    /// <summary>
    /// A signed-in user is not asked to log in again: the page offers their own landing page, keeps the logout control,
    /// and neither shows nor follows the denied path.
    /// </summary>
    [Theory]
    [MemberData(nameof(Roles))]
    public async Task AccessDenied_ForASignedInUser_OffersTheirLandingPage_NotALogin(UserRole role)
    {
        var user = Seed(role);
        using var client = Alpha();
        await PostLoginAsync(client, user.Email);
        Culture(client, "en-US");

        var response = await client.GetAsync("/auth/access-denied?ReturnUrl=%2Fsettings%2Fplatform-secret");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"href=\"{ExpectedLanding(role)}\" data-access-denied-home>Go to my start page</a>", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/auth/logout\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/auth/login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action=\"/auth/login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("platform-secret", AccessDeniedCard(html), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExpectedLanding(role))).StatusCode);
    }

    /// <summary>
    /// Without a session the Access denied page is not shown: the user gets the login page, and after signing in lands
    /// on their own page instead of being sent back to Access denied.
    /// </summary>
    [Fact]
    public async Task AccessDenied_Anonymous_GoesToLogin_AndLoginDoesNotReturnToAccessDenied()
    {
        var kitchen = Seed(UserRole.Kitchen);
        using var client = Alpha();

        var challenge = await client.GetAsync("/auth/access-denied");
        var (loginPath, loginStatus, _) = await FollowAsync(client, Location(challenge));
        var landing = await PostLoginAsync(client, kitchen.Email, "/auth/access-denied");

        Assert.Equal("/auth/login?ReturnUrl=%2Fauth%2Faccess-denied", loginPath);
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        Assert.Equal(LiveScreen, landing);
    }

    [Theory]
    [InlineData("tr-TR", "ltr", "Başlangıç sayfama git", "Erişim reddedildi")]
    [InlineData("en-US", "ltr", "Go to my start page", "Access denied")]
    [InlineData("ar-SA", "rtl", "الانتقال إلى صفحة البداية", "تم رفض الوصول")]
    [InlineData("ru-RU", "ltr", "Перейти на начальную страницу", "Доступ запрещен")]
    public async Task AccessDenied_IsLocalized_AndArabicIsRightToLeft(string culture, string dir, string action, string title)
    {
        var kitchen = Seed(UserRole.Kitchen);
        using var client = Alpha();
        await PostLoginAsync(client, kitchen.Email);
        Culture(client, culture);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/auth/access-denied"))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Contains($"lang=\"{culture}\" dir=\"{dir}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-access-denied-home>{action}</a>", html, StringComparison.Ordinal);
        Assert.Contains(title, html, StringComparison.Ordinal);
    }

    // --- Sessions that changed ------------------------------------------------------------------------

    /// <summary>
    /// A role changed after sign-in ends the old session (WAS-89): the login page renders for it instead of redirecting,
    /// and signing in again lands on the new role's page.
    /// </summary>
    [Fact]
    public async Task RoleChangedAfterSignIn_OldSessionGetsTheLoginForm_NewSignInUsesTheNewRole()
    {
        var user = Seed(UserRole.Kitchen);
        using var client = Alpha();
        await PostLoginAsync(client, user.Email);

        _host.UpdateUser(_host.AlphaId, user.Id, u => u.Role = UserRole.Manager);
        var stale = await client.GetAsync("/auth/login?returnUrl=%2Fdashboard");
        using var again = Alpha();

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Null(client.AuthCookie);
        Assert.Equal(Dashboard, await PostLoginAsync(again, user.Email, Dashboard));
    }

    [Fact]
    public async Task DeactivatedAfterSignIn_OldSessionGetsTheLoginForm_AndCannotSignInAgain()
    {
        var user = Seed(UserRole.Kitchen);
        using var client = Alpha();
        await PostLoginAsync(client, user.Email);

        _host.UpdateUser(_host.AlphaId, user.Id, u => u.IsActive = false);
        var stale = await client.GetAsync("/auth/login");
        using var again = Alpha();
        var refused = await PostLoginFormAsync(again, user.Email, null);

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Null(client.AuthCookie);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Null(again.AuthCookie);
    }

    // --- Tenant hosts ---------------------------------------------------------------------------------

    /// <summary>
    /// Sign-in and redirects stay on the tenant's own host: another tenant's host neither accepts the user nor follows a
    /// session copied from the first host, and a ReturnUrl naming the other host is not followed.
    /// </summary>
    [Fact]
    public async Task LoginAndRedirects_StayOnTheTenantsOwnHost()
    {
        var kitchen = Seed(UserRole.Kitchen);
        using var alpha = Alpha();
        var landing = await PostLoginAsync(alpha, kitchen.Email, "http://beta.wasla.local/orders");

        using var replay = alpha.ReplayTo(_host.BaseAddress, TenantSessionWebHost.BetaHost);
        var copied = await replay.GetAsync("/auth/login?returnUrl=%2Forders");
        using var beta = _host.Client(TenantSessionWebHost.BetaHost);
        var wrongTenant = await PostLoginFormAsync(beta, kitchen.Email, "/orders");

        Assert.Equal(LiveScreen, landing);
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        Assert.Null(copied.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, wrongTenant.StatusCode);
        Assert.Null(beta.AuthCookie);
    }

    /// <summary>
    /// Another tenant's order id in a ReturnUrl is treated like any order id: the redirect is decided by the route and
    /// the role alone, and the order page answers the same for an unknown id, so nothing about the other tenant leaks.
    /// </summary>
    [Fact]
    public async Task ReturnUrl_ToAnotherTenantsOrder_RevealsNothing()
    {
        var foreign = TenantSeed.Order(DateTime.UtcNow.AddMinutes(-5), OrderStatus.New, "BETA-1");
        _host.Tenants.Seed(_host.BetaId, db => db.Orders.Add(foreign));
        var kitchen = Seed(UserRole.Kitchen);

        using var foreignClient = Alpha();
        var foreignLocation = await PostLoginAsync(foreignClient, kitchen.Email, $"/orders/details/{foreign.Id}");
        var foreignPage = await foreignClient.GetAsync(foreignLocation);
        using var randomClient = Alpha();
        var randomLocation = await PostLoginAsync(randomClient, kitchen.Email, $"/orders/details/{Guid.NewGuid()}");
        var randomPage = await randomClient.GetAsync(randomLocation);

        Assert.Equal($"/orders/details/{foreign.Id}", foreignLocation);
        Assert.Equal(randomPage.StatusCode, foreignPage.StatusCode);
        Assert.Equal(
            (await randomPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Length,
            (await foreignPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Length);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static string ExpectedLanding(UserRole role) =>
        role is UserRole.Kitchen or UserRole.Cashier ? LiveScreen : Dashboard;

    private AppUser Seed(UserRole role) =>
        _host.SeedUser(_host.AlphaId, $"{role.ToString().ToLowerInvariant()}@alpha.test", role);

    private TenantSessionClient Alpha() => _host.Client(TenantSessionWebHost.AlphaHost);

    private static void Culture(TenantSessionClient client, string culture) =>
        client.Cookies[CookieRequestCultureProvider.DefaultCookieName] =
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)));

    private static string Location(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    // The page's own card; the layout around it (language links) carries the requested URL like on every page.
    private static string AccessDeniedCard(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, @"<div class=""card wasla-card text-center"">.*?</div>\s*</div>\s*</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, "No Access denied card.");
        return match.Value;
    }

    private static string Printable(string value) =>
        string.Concat(value.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()));

    private static async Task<(string Path, HttpStatusCode Status, List<string> Chain)> FollowAsync(
        TenantSessionClient client, string location)
    {
        var chain = new List<string> { location };
        for (var hop = 0; hop < 10; hop++)
        {
            var response = await client.GetAsync(location);
            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.RedirectKeepVerb))
                return (location, response.StatusCode, chain);

            location = SameHostPath(client, response.Headers.Location!);
            chain.Add(location);
        }

        throw new Xunit.Sdk.XunitException("Redirect loop: " + string.Join(" -> ", chain));
    }

    // The cookie handler redirects with an absolute URL on the request's own host; anything else is a failure.
    private static string SameHostPath(TenantSessionClient client, Uri location)
    {
        if (!location.IsAbsoluteUri)
            return location.OriginalString;

        Assert.Equal(client.Host, location.Host);
        return location.PathAndQuery;
    }

    /// <summary>A successful sign-in; returns the post-login redirect.</summary>
    private static async Task<string> PostLoginAsync(TenantSessionClient client, string email, string? returnUrl = null)
    {
        var response = await PostLoginFormAsync(client, email, returnUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(client.AuthCookie);
        return response.Headers.Location!.OriginalString;
    }

    private static async Task<HttpResponseMessage> PostLoginFormAsync(TenantSessionClient client, string email, string? returnUrl)
    {
        var token = await client.AntiforgeryTokenAsync("/auth/login");
        var form = new List<KeyValuePair<string, string>>
        {
            new("Email", email),
            new("Password", TenantSessionWebHost.Password),
            new("RememberMe", "false"),
            new("__RequestVerificationToken", token)
        };
        if (returnUrl is not null)
            form.Add(new("ReturnUrl", returnUrl));

        return await client.PostFormAsync("/auth/login", form);
    }

    internal static async Task<TenantSessionWebHost> StartHostAsync(Action<IServiceCollection>? configureServices = null)
    {
        // Several tests sign in many times from one address; the login rate limit itself is covered by WebLoginRateLimitTests.
        var host = await TenantSessionWebHost.StartAsync(
            configuration: new Dictionary<string, string?> { ["AuthenticationRateLimiting:TenantLogin:PermitLimit"] = "10000" },
            configureServices: configureServices);
        // The Dashboard sums decimals, which SQLite cannot; read them as doubles so the landing page renders.
        foreach (var tenantId in new[] { host.AlphaId, host.BetaId })
        {
            var connectionString = host.Tenants.ConnectionStringFor(tenantId);
            host.Tenants.Override(tenantId, _ => Task.FromResult<TenantDbContext>(new SqliteAggregateTenantDbContext(connectionString)));
        }

        return host;
    }

    /// <summary>Test data only: the product keeps its decimal columns (as in <c>TenantVisualWebHost</c>).</summary>
    private sealed class SqliteAggregateTenantDbContext(string connectionString)
        : TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connectionString).Options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.Properties<decimal>().HaveConversion<double>();
        }
    }
}
