using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Security;
using OrderHub.Infrastructure.DependencyInjection;
using OrderHub.Web.Localization;
using OrderHub.Web.Middleware;
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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "orderhub_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/auth/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/auth/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services
    .AddControllersWithViews()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization();

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
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
