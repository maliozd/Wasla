using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.Security;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Infrastructure.Realtime;
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

// Orders, automatic approvals and receipt jobs this process stores are announced to Wasla.Web's open screens
// (Realtime:Internal). Outside Development it must be configured or explicitly disabled, or startup stops (WAS-103).
builder.Services.AddWaslaWebTenantChangeNotifier(builder.Configuration, builder.Environment);

builder.Services.AddScoped<OrderSyncService>();
builder.Services.AddScoped<IOrderSyncService>(sp => sp.GetRequiredService<OrderSyncService>());

// Trendyol GO Meal webhook stage pilot: processes stored events; exits at once while the pilot is off.
builder.Services.AddTrendyolGoMealWebhookPilotProcessing(builder.Configuration);
builder.Services.AddHostedService<TrendyolGoMealWebhookWorker>();

builder.Services.AddHostedService<OrderSyncWorker>();

// Guided order training only: moves a practice order when its stage is due, between order-sync cycles.
builder.Services.AddSingleton<GuidedDemoSchedule>();
builder.Services.AddHostedService<GuidedDemoScheduler>();

var host = builder.Build();
host.Services.LogWebTenantChangeChannel(builder.Configuration, "Wasla.Worker");

await host.RunAsync();

