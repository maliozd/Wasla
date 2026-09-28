using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Wasla.Api.Middleware;
using Wasla.Api.Tenant;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Sync;
using Serilog;
using Serilog.Events;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: WaslaLogOutput.Template)
    .WriteTo.File("logs/wasla-api-.log", rollingInterval: RollingInterval.Day, outputTemplate: WaslaLogOutput.Template)
    .CreateBootstrapLogger();

builder.Host.UseSerilog((ctx, services, cfg) =>
{
    cfg.ReadFrom.Services(services)
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: WaslaLogOutput.Template)
        .WriteTo.File("logs/wasla-api-.log", rollingInterval: RollingInterval.Day, outputTemplate: WaslaLogOutput.Template);
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

try
{
    var keyPath = builder.Configuration["DataProtection:KeyPath"];
    if (string.IsNullOrWhiteSpace(keyPath))
        keyPath = @"C:\Wasla-keys";

    var keyDir = new DirectoryInfo(keyPath);
    if (!keyDir.Exists)
    {
        Directory.CreateDirectory(keyDir.FullName);
    }

    builder.Services.AddDataProtection()
        .SetApplicationName("Wasla")
        .PersistKeysToFileSystem(keyDir);
}
catch
{
    // Fallback gracefully: if key persistence fails, default DP settings apply.
    builder.Services.AddDataProtection()
        .SetApplicationName("Wasla");
}

builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();

builder.Services.AddScoped<IOrderSyncService, OrderSyncService>();

builder.Services.AddWaslaInfrastructure(builder.Configuration);
builder.Services.AddWaslaHealthChecks();
builder.Services.Configure<RequestDiagnosticsOptions>(options =>
{
    options.LogRequestCompletion = false;
});

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

app.UseSerilogRequestLogging(options =>
{
    options.IncludeQueryInRequestPath = false;
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms TraceId={TraceId} TenantId={TenantId}";
    options.GetLevel = (httpContext, _, exception) =>
    {
        if (exception is not null || httpContext.Response.StatusCode >= 500)
            return LogEventLevel.Error;
        if (RequestDiagnosticPaths.LogCompletionAtDebug(httpContext.Request.Path, httpContext.Response.StatusCode))
            return LogEventLevel.Debug;
        return LogEventLevel.Information;
    };
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        if (httpContext.Items.TryGetValue(RequestLogState.ItemKey, out var value) && value is RequestLogState state)
        {
            diagnosticContext.Set("TraceId", state.TraceId);
            if (state.TenantId is not null)
                diagnosticContext.Set("TenantId", state.TenantId);
        }
    };
});
app.UseMiddleware<RequestDiagnosticsMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<PrintBridgeAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Ok("Wasla API"));
app.MapWaslaHealthChecks();

app.Run();

