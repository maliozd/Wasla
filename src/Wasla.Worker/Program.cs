using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.Security;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Sync;
using Wasla.Worker.Jobs;
using Serilog;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;

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

builder.Services.AddWaslaInfrastructure(builder.Configuration);

builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddHostedService<OrderSyncWorker>();

var host = builder.Build();

await host.RunAsync();

