using Microsoft.Extensions.Hosting;
using OrderHub.Infrastructure.Security;
using OrderHub.Application.Abstractions.Orders.Services;
using OrderHub.Infrastructure.DependencyInjection;
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

builder.Services.AddMemoryCache();

builder.Services.AddOrderHubInfrastructure(builder.Configuration);

builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddHostedService<OrderSyncWorker>();

var host = builder.Build();

await host.RunAsync();

