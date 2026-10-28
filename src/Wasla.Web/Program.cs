using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Security;
using Wasla.Web;
using Wasla.Web.DevelopmentTools;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Localization;
using Wasla.Web.Middleware;
using Wasla.Web.Realtime;
using Wasla.Web.Routing;
using Wasla.Web.Security;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Web.Tenant;

// Web needs encryption master key to decrypt CustomerDb connection strings
AesSecretManager.ValidateMasterKeyOrThrow();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.Configure<RequestLocalizationOptions>(WaslaRequestLocalization.Configure);

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
})
// Tenant cookie scheme; every tenant request is revalidated against the user in the tenant database.
.AddWaslaTenantCookie(authCookieSecurePolicy)
// Central Admin cookie scheme; every Central Admin request is revalidated against the admin in CentralDb.
.AddWaslaCentralAdminCookie(authCookieSecurePolicy);

builder.Services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
builder.Services.AddScoped<ITenantNavigationAuthorizationService, TenantNavigationAuthorizationService>();
// Post-login destination: a safe ReturnUrl the user may open, otherwise the role's landing page (WAS-113).
builder.Services.AddWaslaTenantLandingPages();
builder.Services.AddScoped<IGuidedSetupCoordinator, GuidedSetupCoordinator>();

// Tenant role policies, shared with Wasla.Api (WaslaTenantPolicies.AllowedRoles).
builder.Services.AddAuthorization(options => options.AddWaslaTenantRolePolicies());

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

// Tenant and Central Admin login attempts per tenant and client network (AuthenticationRateLimiting section).
builder.Services.AddWaslaWebAuthenticationRateLimiting(builder.Configuration);

// X-Forwarded-For / X-Forwarded-Proto only from the proxies listed under TrustedProxies.
builder.Services.AddWaslaTrustedProxies(builder.Configuration);

// Unknown tenant hosts: one CentralDb lookup per host per short TTL, bounded in size (TenantResolution:MissCache).
builder.Services.AddWaslaTenantHostMissCache(builder.Configuration);

// The marketing host shows only the coming-soon page until Wasla:MarketingSite:FullSiteEnabled publishes the full site.
builder.Services.Configure<MarketingSiteOptions>(builder.Configuration.GetSection(MarketingSiteOptions.SectionName));

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
// The public contact form: messages are stored in CentralDb and emailed to Wasla:Contact:RecipientEmail.
builder.Services.AddWaslaContactMessages(builder.Configuration);
// Marketing and application host domains: outside Development they must be set to public host names (WAS-66).
builder.Services.AddWaslaHostDomains(builder.Configuration, builder.Environment);
builder.Services.AddWaslaHealthChecks();

// Live tenant notifications: the SignalR hub and, with Realtime:Internal, the loopback listener for Worker and Api.
builder.Services.AddWaslaRealtime(builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation(
    "Environment={Environment}, AuthCookieSecurePolicy={SecurePolicy}",
    app.Environment.EnvironmentName,
    authCookieSecurePolicy);
app.Services.LogInternalListenerState(builder.Configuration);

// First, so that everything after it (rate-limit partitions, HTTPS redirection, logs) sees the forwarded client.
app.UseWaslaTrustedProxies();
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

// Live hub requests from another origin are refused before any tenant or session work.
app.UseWaslaRealtimeOriginCheck();

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
app.MapWaslaRealtime();
app.EnableWaslaTenantLandingPages();

app.Run();
