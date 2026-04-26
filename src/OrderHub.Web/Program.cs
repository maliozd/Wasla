using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Security;
using OrderHub.Infrastructure.DependencyInjection;
using OrderHub.Web;
using OrderHub.Web.Localization;
using OrderHub.Web.Middleware;
using OrderHub.Web.Security;
using OrderHub.Web.Tenant;
using System.Globalization;

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
    .SetApplicationName("OrderHub");

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

var securePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = AuthSchemes.Customer;
        options.DefaultAuthenticateScheme = AuthSchemes.Customer;
        options.DefaultChallengeScheme = AuthSchemes.Customer;
    })
    .AddCookie(AuthSchemes.Customer, options =>
    {
        options.Cookie.Name = "orderhub_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = securePolicy;
        options.LoginPath = "/auth/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/auth/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    })
    .AddCookie(AuthSchemes.CentralAdmin, options =>
    {
        options.Cookie.Name = "orderhub_central_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = securePolicy;
        options.LoginPath = "/admin/login";
        options.LogoutPath = "/admin/logout";
        options.AccessDeniedPath = "/admin/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services
    .AddControllersWithViews()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
    {
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource));
    });

builder.Services.AddScoped<ICurrentCustomerService, CurrentCustomerService>();
builder.Services.AddOrderHubInfrastructure(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/auth/login");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

var locOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>();
app.UseRequestLocalization(locOptions.Value);

app.UseRouting();

app.UseMiddleware<CustomerResolutionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
