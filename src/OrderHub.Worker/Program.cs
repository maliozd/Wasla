using Microsoft.Extensions.Hosting;
using OrderHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Application.Abstractions.Platform;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Infrastructure.Platform.Mapping;
using OrderHub.Infrastructure.Platform.Mock;
using OrderHub.Infrastructure.Sync;
using OrderHub.Worker.Jobs;
using Serilog;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = Host.CreateApplicationBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/orderhub-worker-.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

builder.Services.AddSerilog((services, cfg) =>
{
    cfg.ReadFrom.Services(services)
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/orderhub-worker-.log", rollingInterval: RollingInterval.Day);
});

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICustomerDbContextFactory, CustomerDbContextFactory>();

builder.Services.AddSingleton<IFoodPlatformClient, YemeksepetiFoodPlatformClient>();
builder.Services.AddSingleton<IFoodPlatformClient, GetirYemekFoodPlatformClient>();
builder.Services.AddSingleton<IFoodPlatformClient, TrendyolYemekFoodPlatformClient>();
builder.Services.AddSingleton<IOrderStatusMapper, DefaultOrderStatusMapper>();

builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddHostedService<OrderSyncWorker>();

var host = builder.Build();

await host.RunAsync();

