using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
using FluentValidation;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Demos;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Admin;
using Wasla.Web;
using Wasla.Web.GuidedSetup;
using Wasla.Infrastructure.Security;
using Wasla.Web.Security;
using CurrentTenantService = Wasla.Web.Tenant.CurrentTenantService;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Serves real tenant pages (the <c>_TenantLayout</c> shell through Help and Guides, the Live Screen on
/// <c>_OrdersDisplayLayout</c> with no orders, and the tenant sign-in page) and real
/// Central Admin pages from <em>one</em> loopback origin, as production does when <c>/admin</c> is opened on a tenant
/// host. Compiled Razor views and the real wwwroot are used, with the same two cookie schemes and tenant role policies
/// as Program.cs. Tenant resolution is replaced by one fixed tenant for every non-<c>/admin</c> request. The test-only
/// endpoints issue a tenant (Owner) or Central Admin cookie; no password is involved.
/// </summary>
internal sealed class ThemeWebHost : IAsyncDisposable
{
    public const string TenantSignInPath = "/__test/tenant-sign-in";
    public const string AdminSignInPath = "/__test/admin-sign-in";

    public static readonly Guid TenantId = Guid.Parse("4b1d2c70-5c3e-4a8e-9f0a-45a5c0a1e045");

    private readonly WebApplication _app;
    private readonly string _contentRoot;
    private readonly CentralTestDatabase _central;
    private readonly RecordingTenantDbFactory _tenants;

    private ThemeWebHost(WebApplication app, string contentRoot, CentralTestDatabase central, RecordingTenantDbFactory tenants)
    {
        _app = app;
        _contentRoot = contentRoot;
        _central = central;
        _tenants = tenants;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    public static async Task<ThemeWebHost> StartAsync()
    {
        var central = new CentralTestDatabase();
        var tenants = new RecordingTenantDbFactory();
        var contentRoot = Directory.CreateTempSubdirectory("wasla-theme-host-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(SharedResource).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            // The real stylesheets and scripts, so a real browser renders the pages as deployed.
            WebRootPath = TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot")
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["Platforms:ProviderMode"] = "Mock";

        var services = builder.Services;
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
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
            });

        // Same tenant role policies as Program.cs.
        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        services.AddScoped<ITenantNavigationAuthorizationService, TenantNavigationAuthorizationService>();
        services.AddAuthorization(options =>
        {
            AddTenantRolePolicy(options, TenantPolicies.TenantOwner, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.TenantManagerOrOwner, UserRole.Owner, UserRole.Manager);
            AddTenantRolePolicy(options, TenantPolicies.CanManageTenantUsers, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanManageTenantSettings, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
            AddTenantRolePolicy(options, TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddTenantRolePolicy(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            AddTenantRolePolicy(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
            AddTenantRolePolicy(options, TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddTenantRolePolicy(options, TenantPolicies.CanViewReports, UserRole.Owner, UserRole.Manager, UserRole.Viewer);
        });
        services.AddControllersWithViews()
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization(options =>
                options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

        // The tenant sign-in page is only rendered (GET); none of its services is called.
        services.AddSingleton(Unused<IAuthValidationService>.Create());
        services.AddSingleton(Unused<ITenantLoginRecorder>.Create());
        services.AddSingleton(Unused<ISignupCompletionTokenService>.Create());
        services.AddSingleton(Unused<ITenantPasswordResetService>.Create());

        // The Live Screen page is rendered with no orders; its live-data polling and order actions are not served.
        services.AddSingleton(NoOrders.Create());
        services.AddSingleton(Unused<IOrderActionService>.Create());
        services.AddSingleton(Unused<IOrderSyncSettingsService>.Create());
        services.AddSingleton(Unused<ITenantOrderSettingsService>.Create());
        services.AddSingleton(Unused<IOrderReceiptCreationService>.Create());
        services.AddSingleton(Unused<IManualOrderPrintService>.Create());
        services.AddSingleton(Unused<IGuidedDemoService>.Create());
        services.AddSingleton(Unused<IValidator<UpdateTenantOrderSettingsCommand>>.Create());
        services.AddSingleton(Unused<IGuidedSetupCoordinator>.Create());

        // Central Admin dashboard, as AdminWebHost registers it.
        services.AddDbContext<CentralDbContext>(options => options.UseSqlite(central.ConnectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddScoped<ICentralAdminTenantOperationsService, CentralAdminTenantOperationsService>();
        services.AddScoped<ICentralAdminPendingRegistrationService, CentralAdminPendingRegistrationService>();
        services.AddSingleton<ITenantDbContextFactory>(tenants);
        services.Configure<TenantOperationalHealthOptions>(options => options.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<ITenantOperationalHealthReader, TenantOperationalHealthReader>();

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseRequestLocalization();
        app.UseRouting();

        // Stands in for TenantResolutionMiddleware: /admin is not tenant-resolved, everything else is this tenant.
        var tenant = new ResolvedTenantDto(TenantId, "Theme Test Restaurant", "theme-test", "127.0.0.1");
        app.Use((http, next) =>
        {
            if (!http.Request.Path.StartsWithSegments("/admin"))
                http.Items["CurrentTenant"] = tenant;
            return next(http);
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(TenantSignInPath, async (HttpContext http) =>
        {
            var identity = new ClaimsIdentity(
                [
                    // Not a user id: the Live Screen then skips the per-user guided-training lookup.
                    new Claim(ClaimTypes.NameIdentifier, "theme-owner"),
                    new Claim(ClaimTypes.Name, "Theme Owner"),
                    new Claim("TenantId", TenantId.ToString()),
                    new Claim(ClaimTypes.Role, nameof(UserRole.Owner))
                ],
                AuthSchemes.Tenant);
            await http.SignInAsync(AuthSchemes.Tenant, new ClaimsPrincipal(identity));
            return Results.Ok();
        });
        app.MapGet(AdminSignInPath, async (HttpContext http) =>
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Email, "ops@wasla.test"),
                    new Claim(ClaimTypes.Name, "Ops"),
                    new Claim(ClaimTypes.Role, "CentralAdmin")
                ],
                AuthSchemes.CentralAdmin);
            await http.SignInAsync(AuthSchemes.CentralAdmin, new ClaimsPrincipal(identity));
            return Results.Ok();
        });

        app.MapControllers();
        app.MapControllerRoute(name: "areas", pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

        await app.StartAsync();
        return new ThemeWebHost(app, contentRoot, central, tenants);
    }

    /// <summary>
    /// A real browser (desktop viewport) with the culture cookie for this origin and, when <paramref name="signedIn"/>,
    /// both a tenant session and a Central Admin session. Null when no Chromium browser is installed.
    /// </summary>
    public async Task<HeadlessChromium?> BrowserAsync(string culture, bool signedIn, CancellationToken ct)
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            return null;

        var cookies = new System.Net.CookieContainer();
        if (signedIn)
        {
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { BaseAddress = BaseAddress };
            (await client.GetAsync(TenantSignInPath, ct)).EnsureSuccessStatusCode();
            (await client.GetAsync(AdminSignInPath, ct)).EnsureSuccessStatusCode();
        }

        var browser = await HeadlessChromium.StartAsync(executable, ct);
        foreach (System.Net.Cookie cookie in cookies.GetCookies(BaseAddress))
            await browser.SetCookieAsync(BaseAddress, cookie.Name, cookie.Value, ct);
        await browser.SetCookieAsync(BaseAddress, CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))), ct);
        await browser.SetViewportAsync(1280, 860, mobile: false, ct);
        return browser;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }

    // Mirrors the internal TenantAuthorizationPolicyExtensions.AddTenantRolePolicy used by Program.cs.
    private static void AddTenantRolePolicy(AuthorizationOptions options, string name, params UserRole[] roles) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new TenantRoleRequirement(roles));
        });

    /// <summary>An order read service with no orders (the Live Screen's first render).</summary>
    public class NoOrders : DispatchProxy
    {
        public static IOrderReadService Create() => DispatchProxy.Create<IOrderReadService, NoOrders>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IOrderReadService.GetListAsync)
                ? Task.FromResult(new OrderListResult { Items = [], TotalCount = 0, Page = (int)args![7]!, PageSize = (int)args[8]! })
                : throw new NotSupportedException($"{nameof(IOrderReadService)}.{targetMethod?.Name} is not part of the theme tests.");
    }

    /// <summary>A service the served pages need in DI but never call.</summary>
    public class Unused<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, Unused<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{typeof(T).Name}.{targetMethod?.Name} is not part of the theme tests.");
    }
}
