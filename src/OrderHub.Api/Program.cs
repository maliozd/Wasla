using Microsoft.AspNetCore.Authentication.Cookies;
using OrderHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Middleware;
using OrderHub.Api.Tenant;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Application.Abstractions.Persistence;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Application.Auth.Services;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Infrastructure.Platform.Mapping;
using OrderHub.Infrastructure.Platform.Mock;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Services;
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

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();
builder.Services.AddScoped<ICurrentCustomerService, CurrentCustomerService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddSingleton<ICustomerDbContextFactory, CustomerDbContextFactory>();
builder.Services.AddSingleton<IFoodPlatformClient, YemeksepetiFoodPlatformClient>();
builder.Services.AddSingleton<IFoodPlatformClient, GetirYemekFoodPlatformClient>();
builder.Services.AddSingleton<IFoodPlatformClient, TrendyolYemekFoodPlatformClient>();
builder.Services.AddSingleton<IOrderStatusMapper, DefaultOrderStatusMapper>();
builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();
builder.Services.AddAuthorization();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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

