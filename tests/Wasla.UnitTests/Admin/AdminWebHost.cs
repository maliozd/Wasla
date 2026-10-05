using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Security;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Web;
using Wasla.Web.Areas.Admin.Controllers;
using Wasla.Web.Middleware;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// Serves the real Admin controllers and compiled Razor views of Wasla.Web over loopback HTTP, with the same two cookie
/// schemes as Program.cs, a SQLite CentralDb and SQLite tenant databases behind <see cref="ITenantDbContextFactory"/>.
/// Central admins sign in through the real <c>/admin/login</c> form. The only test-only endpoint issues a
/// <em>tenant</em> cookie, to prove a tenant session cannot reach Admin pages.
/// </summary>
internal sealed class AdminWebHost : IAsyncDisposable
{
    public const string AdminEmail = "ops@wasla.test";
    public const string AdminPassword = "Ops-Test-Password-42";
    public const string TenantSignInPath = "/__test/tenant-sign-in";
    public const string CentralAdminCookiePath = "/__test/central-admin-cookie";

    private readonly WebApplication _app;
    private readonly string _contentRoot;

    private AdminWebHost(WebApplication app, string contentRoot)
    {
        _app = app;
        _contentRoot = contentRoot;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    public static async Task<AdminWebHost> StartAsync(
        CentralTestDatabase central,
        RecordingTenantDbFactory tenants,
        TimeSpan? healthTimeout = null,
        TimeProvider? centralAdminCookieClock = null)
    {
        SeedAdmins(central);

        var contentRoot = Directory.CreateTempSubdirectory("wasla-admin-host-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(CustomersController).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            // The real stylesheets and scripts, so a real browser renders the pages as deployed.
            WebRootPath = TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot")
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["Platforms:ProviderMode"] = "Mock";

        var services = builder.Services;
        services.AddMemoryCache();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(options =>
        {
            var supported = new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" }.Select(CultureInfo.GetCultureInfo).ToList();
            options.DefaultRequestCulture = new RequestCulture("tr-TR");
            options.SupportedCultures = supported;
            options.SupportedUICultures = supported;
            options.RequestCultureProviders = new List<IRequestCultureProvider> { new CookieRequestCultureProvider() };
        });
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery();

        // Same scheme contract as Program.cs.
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = AuthSchemes.Tenant;
                options.DefaultAuthenticateScheme = AuthSchemes.Tenant;
                options.DefaultChallengeScheme = AuthSchemes.Tenant;
            })
            .AddCookie(AuthSchemes.Tenant, options =>
            {
                options.Cookie.Name = TenantAuthCookieNames.Active;
                options.LoginPath = "/auth/login";
                options.AccessDeniedPath = "/auth/access-denied";
            })
            .AddCookie(AuthSchemes.CentralAdmin, options =>
            {
                options.Cookie.Name = CentralAdminAuthCookieNames.Active;
                options.LoginPath = "/admin/login";
                options.LogoutPath = "/admin/logout";
                options.AccessDeniedPath = "/admin/login";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.EventsType = typeof(CentralAdminCookieEvents);
                // Lets tests move the cookie handler's clock to trigger sliding renewal; null keeps the system clock.
                options.TimeProvider = centralAdminCookieClock;
            });
        services.AddScoped<CentralAdminCookieEvents>();
        services.AddAuthorization();
        services.AddControllersWithViews()
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization(options =>
                options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

        services.AddDbContext<CentralDbContext>(options => options.UseSqlite(central.ConnectionString).AddInterceptors(central.Counter));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddScoped<ICentralAdminAuthService, CentralAdminAuthService>();
        services.AddScoped<ICentralAdminSessionValidator, CentralAdminSessionValidator>();
        services.AddScoped<ICentralAdminTenantService, CentralAdminTenantService>();
        services.AddScoped<ICentralAdminTenantOperationsService, CentralAdminTenantOperationsService>();
        services.AddScoped<ICentralAdminPendingRegistrationService, CentralAdminPendingRegistrationService>();
        // The pending-registrations pages are served for layout checks only; provisioning is never invoked.
        services.AddSingleton<IPendingRegistrationProvisioningService, NoProvisioning>();
        services.AddSingleton<ITenantDbContextFactory>(tenants);
        services.Configure<TenantOperationalHealthOptions>(options => options.Timeout = healthTimeout ?? TimeSpan.FromSeconds(10));
        services.AddSingleton<ITenantOperationalHealthReader, TenantOperationalHealthReader>();

        var app = builder.Build();
        // The same global error handling as Program.cs (Production: re-executes /error).
        app.UseWaslaExceptionHandling(app.Environment);
        app.UseStaticFiles();
        app.UseRequestLocalization();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        // Test-only: a tenant session for some tenant, with a forged "CentralAdmin" role claim on top.
        app.MapGet(TenantSignInPath, async (HttpContext http) =>
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("TenantId", http.Request.Query["tenantId"].ToString()),
                    new Claim(ClaimTypes.Role, "Owner"),
                    new Claim(ClaimTypes.Role, "CentralAdmin")
                ],
                AuthSchemes.Tenant);
            await http.SignInAsync(AuthSchemes.Tenant, new ClaimsPrincipal(identity));
            return Results.Ok();
        });

        // Test-only: a real, correctly protected Central Admin cookie with chosen claims, so tests can present cookies
        // that a pre-stamp release or a tampered issuer would have produced. Omitted query values omit the claim.
        app.MapGet(CentralAdminCookiePath, async (HttpContext http) =>
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Email, AdminEmail),
                new(ClaimTypes.Name, "Ops"),
                new(ClaimTypes.Role, "CentralAdmin")
            };
            if (http.Request.Query.TryGetValue("adminId", out var adminId))
                claims.Add(new Claim(ClaimTypes.NameIdentifier, adminId.ToString()));
            if (http.Request.Query.TryGetValue("stamp", out var stamp))
                claims.Add(new Claim(WaslaAuthContracts.CentralAdminSecurityStampClaim, stamp.ToString()));

            await http.SignInAsync(
                AuthSchemes.CentralAdmin,
                new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.CentralAdmin)),
                AuthCookiePersistence.Create(rememberMe: false, AuthCookiePersistence.CentralAdminPersistentDuration));
            return Results.Ok();
        });

        app.MapControllers();
        app.MapControllerRoute(name: "areas", pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

        await app.StartAsync();
        return new AdminWebHost(app, contentRoot);
    }

    public AdminBrowser NewBrowser() => new(BaseAddress);

    public async Task<AdminBrowser> SignedInAdminAsync(string? culture = null)
    {
        var browser = NewBrowser();
        if (culture is not null)
            browser.SetCulture(culture);
        await browser.SignInAdminAsync();
        return browser;
    }

    private static void SeedAdmins(CentralTestDatabase central)
    {
        using var db = central.CreateContext();
        if (db.CentralAdminUsers.Any())
            return;

        db.CentralAdminUsers.Add(new CentralAdminUser
        {
            Email = AdminEmail,
            NormalizedEmail = AdminEmail.ToUpperInvariant(),
            DisplayName = "Ops",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(AdminPassword, workFactor: 4),
            IsActive = true
        });
        db.CentralAdminUsers.Add(new CentralAdminUser
        {
            Email = "other-admin@wasla.test",
            NormalizedEmail = "OTHER-ADMIN@WASLA.TEST",
            DisplayName = "Other",
            PasswordHash = SecretMarkers.AdminPasswordHash,
            IsActive = true
        });
        db.SaveChanges();
    }

    private sealed class NoProvisioning : IPendingRegistrationProvisioningService
    {
        public Task<ProvisioningResult> ProvisionAsync(
            Guid registrationId, bool force = false, string? sqlServerOverride = null, string? sqlAuthOverride = null,
            string? panelLoginUrl = null, CancellationToken ct = default) =>
            throw new NotSupportedException("Provisioning is not part of the Admin operations tests.");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }
}

internal sealed record AdminResponse(HttpStatusCode Status, string Body, Uri? Location, IReadOnlyList<string>? SetCookies = null);

/// <summary>A browser with its own cookie jar that does not follow redirects.</summary>
internal sealed partial class AdminBrowser : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;
    private readonly Uri _baseAddress;

    public AdminBrowser(Uri baseAddress)
    {
        _baseAddress = baseAddress;
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = _cookies, UseCookies = true })
        {
            BaseAddress = baseAddress
        };
    }

    /// <summary>The cookies this browser holds for the host, for handing a signed-in session to a real browser.</summary>
    public IReadOnlyList<Cookie> Cookies => _cookies.GetCookies(_baseAddress).ToList();

    public void SetCulture(string culture) =>
        _cookies.Add(_baseAddress, new Cookie(
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)))));

    public async Task<AdminResponse> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        return new AdminResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            response.Headers.Location,
            response.Headers.TryGetValues("Set-Cookie", out var setCookies) ? setCookies.ToList() : []);
    }

    /// <summary>The Central Admin session cookie this browser currently holds, or null.</summary>
    public Cookie? CentralAdminCookie => _cookies.GetCookies(_baseAddress)[CentralAdminAuthCookieNames.Active];

    /// <summary>Presents a previously captured Central Admin cookie value, as a copied or stolen cookie would be.</summary>
    public void UseCentralAdminCookie(string value) =>
        _cookies.Add(_baseAddress, new Cookie(CentralAdminAuthCookieNames.Active, value));

    public Task SignInAdminAsync() => SignInAdminAsync(AdminWebHost.AdminEmail, AdminWebHost.AdminPassword);

    public async Task SignInAdminAsync(string email, string password, bool rememberMe = false)
    {
        var login = await GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var token = AntiforgeryToken().Match(login.Body);
        Assert.True(token.Success, "The admin login page has no antiforgery field.");

        var form = new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value)
        };
        if (rememberMe)
            form["RememberMe"] = "true";

        using var response = await _client.PostAsync("/admin/login", new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(_cookies.GetCookies(_baseAddress)[CentralAdminAuthCookieNames.Active]);
    }

    public async Task SignInTenantUserAsync(Guid tenantId)
    {
        var response = await GetAsync($"{AdminWebHost.TenantSignInPath}?tenantId={tenantId}");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.NotNull(_cookies.GetCookies(_baseAddress)[TenantAuthCookieNames.Active]);
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
