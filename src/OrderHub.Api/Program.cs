using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.IO;
using OrderHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Middleware;
using OrderHub.Api.Tenant;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.DependencyInjection;
using OrderHub.Infrastructure.Sync;
using Serilog;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/orderhub-api-.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

builder.Host.UseSerilog((ctx, services, cfg) =>
{
    cfg.ReadFrom.Services(services)
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/orderhub-api-.log", rollingInterval: RollingInterval.Day);
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

try
{
    var keyPath = builder.Configuration["DataProtection:KeyPath"];
    if (string.IsNullOrWhiteSpace(keyPath))
        keyPath = @"C:\OrderHub-keys";

    var keyDir = new DirectoryInfo(keyPath);
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

builder.Services.AddScoped<ICurrentCustomerService, CurrentCustomerService>();

builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddOrderHubInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "orderhub_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CustomerResolutionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Ok("OrderHub API"));

app.Run();

