using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Api;
using Wasla.Api.Security;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Sync;
using ApiCurrentTenantService = Wasla.Api.Tenant.CurrentTenantService;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Serves Wasla.Api in-process next to a <see cref="TenantSessionWebHost"/>: the Api's own controllers, tenant
/// resolution, Print Bridge middleware, tenant cookie scheme and authorization, over the Web host's SQLite CentralDb and
/// tenant databases, and Data Protection on the same key folder and application name as the Web host. A cookie issued by
/// the Web login can therefore be presented to the Api, as it can in a deployment that shares the key ring. Requests
/// carry a tenant host in the Host header. Users, passwords, secrets and data are fake.
/// </summary>
internal sealed class TenantSessionApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _contentRoot;

    private TenantSessionApiHost(WebApplication app, string contentRoot, CapturingLoggerProvider logs)
    {
        _app = app;
        _contentRoot = contentRoot;
        Logs = logs;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    public CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TenantSessionApiHost> StartAsync(TenantSessionWebHost web, DirectoryInfo sharedDataProtectionKeys)
    {
        var contentRoot = Directory.CreateTempSubdirectory("wasla-api-host-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(Wasla.Api.Controllers.AuthController).Assembly.GetName().Name,
            ContentRootPath = contentRoot
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var logs = new CapturingLoggerProvider();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platforms:ProviderMode"] = "Mock",
            // Never opened: CentralDbContext is replaced by SQLite below.
            ["ConnectionStrings:CentralDb"] = "Server=(none);Database=WaslaApiSessionTests"
        });

        // Wasla.Api Program.cs service registrations (Serilog and Swagger left out).
        var services = builder.Services;
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddDataProtection().SetApplicationName("Wasla").PersistKeysToFileSystem(sharedDataProtectionKeys);
        services.AddScoped<ICurrentTenantService, ApiCurrentTenantService>();
        services.AddScoped<IOrderSyncService, OrderSyncService>();
        services.AddWaslaInfrastructure(builder.Configuration);
        services.AddWaslaHealthChecks();
        services.Configure<RequestDiagnosticsOptions>(options => options.LogRequestCompletion = false);
        services.AddWaslaApiTenantAuthentication(CookieSecurePolicy.Always);
        services.AddControllers();

        // The same databases as the Web host, and a fake secret manager instead of the master key.
        services.RemoveAll<DbContextOptions<CentralDbContext>>();
        services.RemoveAll<CentralDbContext>();
        services.AddDbContext<CentralDbContext>(options => options.UseSqlite(web.Central.ConnectionString));
        services.RemoveAll<ITenantDbContextFactory>();
        services.AddSingleton<ITenantDbContextFactory>(web.Tenants);
        services.RemoveAll<ISecretManager>();
        services.AddSingleton<ISecretManager, FakeSecretManager>();

        var app = builder.Build();

        // Wasla.Api Program.cs pipeline: the same extensions, in the same order.
        app.UseMiddleware<RequestDiagnosticsMiddleware>();
        app.UseWaslaApiRequestPipeline();
        app.MapWaslaApiEndpoints();

        await app.StartAsync();
        return new TenantSessionApiHost(app, contentRoot, logs);
    }

    public TenantSessionClient Client(string host) => new(BaseAddress, host);

    /// <summary>
    /// Builds a tenant auth cookie value with the Api's own tenant scheme ticket format and the shared keys, for
    /// principals the Web login does not produce (missing or malformed claims, an old issue time).
    /// </summary>
    public string ProtectTenantCookie(IEnumerable<Claim> claims, DateTimeOffset issuedUtc, DateTimeOffset expiresUtc)
    {
        var options = _app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(WaslaAuthContracts.TenantScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, WaslaAuthContracts.TenantScheme)),
            new AuthenticationProperties { IssuedUtc = issuedUtc, ExpiresUtc = expiresUtc },
            WaslaAuthContracts.TenantScheme);
        return options.TicketDataFormat.Protect(ticket);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for <c>AesSecretManager</c>; the values are fake and never leave the test databases.</summary>
    private sealed class FakeSecretManager : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult(("fake-encrypted:" + plaintext, 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult(encryptedBase64["fake-encrypted:".Length..]);
    }
}
