using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Wasla.Domain.Enums;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Security;
using Wasla.Web;
using Wasla.Web.DevelopmentTools;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Localization;
using Wasla.Web.Middleware;
using Wasla.Web.Security;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Web.Tenant;

// Web needs encryption master key to decrypt CustomerDb connection strings
AesSecretManager.ValidateMasterKeyOrThrow();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supported = SupportedCultures.AllCultureInfos;

    options.DefaultRequestCulture = new RequestCulture(SupportedCultures.Default);
    options.SupportedCultures = supported.ToList();
    options.SupportedUICultures = supported.ToList();

    // Provider priority:
    // 1) Cookie
    // 2) Accept-Language
    // 3) Default fallback

    options.RequestCultureProviders = new List<IRequestCultureProvider>
    {
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    };
});

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Wasla");

var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(keyPath))
{
    try
    {
        var keyDir = new DirectoryInfo(keyPath);
        if (!keyDir.Exists)
            Directory.CreateDirectory(keyDir.FullName);
        dataProtection.PersistKeysToFileSystem(keyDir);
    }
    catch
    {
        // Key persistence unavailable; default DataProtection key storage applies.
    }
}

// Development: allow auth cookies over local HTTP (e.g. *.wasla.local:5200).
// Production: keep HTTPS-only secure cookies.
var authCookieSecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = authCookieSecurePolicy;
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AuthSchemes.Tenant;
    options.DefaultAuthenticateScheme = AuthSchemes.Tenant;
    options.DefaultChallengeScheme = AuthSchemes.Tenant;
}).AddCookie(AuthSchemes.Tenant, options =>
{
    options.Cookie.Name = TenantAuthCookieNames.Active;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = authCookieSecurePolicy;
    options.LoginPath = "/auth/login";
    options.LogoutPath = "/auth/logout";
    options.AccessDeniedPath = "/auth/access-denied";
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
})
.AddCookie(AuthSchemes.CentralAdmin, options =>
{
    options.Cookie.Name = CentralAdminAuthCookieNames.Active;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = authCookieSecurePolicy;
    options.LoginPath = "/admin/login";
    options.LogoutPath = "/admin/logout";
    options.AccessDeniedPath = "/admin/login";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    // Every Central Admin request is revalidated against the account in CentralDb.
    options.EventsType = typeof(CentralAdminCookieEvents);
});

builder.Services.AddScoped<CentralAdminCookieEvents>();
builder.Services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
builder.Services.AddScoped<ITenantNavigationAuthorizationService, TenantNavigationAuthorizationService>();
builder.Services.AddScoped<IGuidedSetupCoordinator, GuidedSetupCoordinator>();

builder.Services.AddAuthorization(options =>
{
    options.AddTenantRolePolicy(TenantPolicies.TenantOwner, UserRole.Owner);
    options.AddTenantRolePolicy(TenantPolicies.TenantManagerOrOwner, UserRole.Owner, UserRole.Manager);
    options.AddTenantRolePolicy(TenantPolicies.CanManageTenantUsers, UserRole.Owner);
    options.AddTenantRolePolicy(TenantPolicies.CanManageTenantSettings, UserRole.Owner);
    options.AddTenantRolePolicy(TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
    options.AddTenantRolePolicy(TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
    options.AddTenantRolePolicy(TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
    options.AddTenantRolePolicy(TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
    options.AddTenantRolePolicy(TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
    options.AddTenantRolePolicy(TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
    options.AddTenantRolePolicy(TenantPolicies.CanViewReports, UserRole.Owner, UserRole.Manager, UserRole.Viewer);
    options.AddPolicy("ManagePlatformConnections", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new TenantRoleRequirement(UserRole.Owner));
    });
});

// Rate limit automatic Print Bridge setup code exchange attempts (per client IP).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.PrintBridgeSetupExchange, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy(RateLimitPolicies.ForgotPassword, httpContext =>
    {
        var host = httpContext.Request.Host.Host?.Trim().ToLowerInvariant() ?? "unknown-host";
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{host}:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
});

// Temporary Development tools: off unless Development and explicitly enabled (appsettings.Development.json).
var developmentToolsSection = builder.Configuration.GetSection(DevelopmentToolsOptions.SectionName);
builder.Services.Configure<DevelopmentToolsOptions>(developmentToolsSection);
var tenantResetAvailable = DevelopmentToolsAvailability.IsTenantResetAvailable(
    builder.Environment,
    developmentToolsSection.Get<DevelopmentToolsOptions>());

builder.Services
    .AddControllersWithViews(options => options.Conventions.Add(new DevelopmentToolsConvention(tenantResetAvailable)))
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
    {
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource));
    });

builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();
builder.Services.AddWaslaInfrastructure(builder.Configuration);
builder.Services.AddWaslaHealthChecks();

var app = builder.Build();

app.Logger.LogInformation(
    "Environment={Environment}, AuthCookieSecurePolicy={SecurePolicy}",
    app.Environment.EnvironmentName,
    authCookieSecurePolicy);

app.UseMiddleware<RequestDiagnosticsMiddleware>();
app.UseWaslaExceptionHandling(app.Environment);

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

var locOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>();
app.UseRequestLocalization(locOptions.Value);

app.UseRouting();

app.UseRateLimiter();

app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<PrintBridgeAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapWaslaHealthChecks();

app.Run();
