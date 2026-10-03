using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.Security;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.Worker.Jobs;
using Serilog;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = Host.CreateApplicationBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: WaslaLogOutput.Template)
    .WriteTo.File("logs/wasla-worker-.log", rollingInterval: RollingInterval.Day, outputTemplate: WaslaLogOutput.Template)
    .CreateBootstrapLogger();

builder.Services.AddSerilog((services, cfg) =>
{
    cfg.ReadFrom.Services(services)
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: WaslaLogOutput.Template)
        .WriteTo.File("logs/wasla-worker-.log", rollingInterval: RollingInterval.Day, outputTemplate: WaslaLogOutput.Template);
});

builder.Services.AddMemoryCache();

builder.Services.AddWaslaInfrastructure(builder.Configuration);

builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddHostedService<OrderSyncWorker>();

// Guided order training only: moves a practice order when its stage is due, between order-sync cycles.
builder.Services.AddSingleton<GuidedDemoSchedule>();
builder.Services.AddHostedService<GuidedDemoScheduler>();

var host = builder.Build();

await host.RunAsync();

