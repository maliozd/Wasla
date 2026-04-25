using System.IO;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using OrderHub.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

builder.Services.AddHttpContextAccessor();

try
{
    var keyDir = new DirectoryInfo(@"C:\OrderHub-keys");
    if (!keyDir.Exists)
    {
        Directory.CreateDirectory(keyDir.FullName);
    }

    builder.Services.AddDataProtection()
        .SetApplicationName("OrderHub")
        .PersistKeysToFileSystem(keyDir);
}
catch
{
    // Fallback gracefully: if key persistence fails, default DP settings apply.
    builder.Services.AddDataProtection()
        .SetApplicationName("OrderHub");
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "orderhub_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/giris";
        options.LogoutPath = "/cikis";
        options.AccessDeniedPath = "/erisim-engeli";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services.AddTransient<CookieForwardingHandler>();

var apiBaseUrl = builder.Configuration["Api:BaseUrl"]
    ?? throw new InvalidOperationException("Api:BaseUrl not configured.");

builder.Services.AddHttpClient<IOrderHubApiClient, OrderHubApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    if (builder.Environment.IsDevelopment())
    {
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
    }
    return handler;
})
.AddHttpMessageHandler<CookieForwardingHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/hata");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
