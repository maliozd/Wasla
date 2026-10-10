using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Api.Security;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

public sealed class ApiTenantAuthenticationTests : IDisposable
{
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "wasla-auth-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public void Dispose()
    {
        if (Directory.Exists(_keyDirectory))
            Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public void ApiProgram_UsesWaslaTenantCookie_AndDoesNotConfigureLegacyCookie()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Wasla.Api", "Program.cs"));
        // Program.cs runs the request pipeline through this extension (WAS-94).
        var pipeline = File.ReadAllText(Path.Combine(root, "src", "Wasla.Api", "WaslaApiPipeline.cs"));
        var authController = File.ReadAllText(Path.Combine(root, "src", "Wasla.Api", "Controllers", "AuthController.cs"));

        Assert.Equal(AuthSchemes.Tenant, WaslaAuthContracts.TenantScheme);
        Assert.Equal(TenantAuthCookieNames.Active, WaslaAuthContracts.TenantCookieName);
        Assert.Equal(AuthSchemes.CentralAdmin, WaslaAuthContracts.CentralAdminScheme);
        Assert.Equal(CentralAdminAuthCookieNames.Active, WaslaAuthContracts.CentralAdminCookieName);
        Assert.Equal("orderhub_auth", WaslaAuthContracts.LegacyTenantCookieName);

        Assert.Contains("AddWaslaApiTenantAuthentication(CookieSecurePolicy.Always)", program, StringComparison.Ordinal);
        Assert.Contains("app.UseWaslaApiRequestPipeline();", program, StringComparison.Ordinal);
        Assert.Contains("app.MapWaslaApiEndpoints();", program, StringComparison.Ordinal);
        Assert.Contains("ExpireLegacyTenantAuthCookieMiddleware", pipeline, StringComparison.Ordinal);
        Assert.Contains("UseMiddleware<TenantResolutionMiddleware>();", pipeline, StringComparison.Ordinal);
        Assert.Contains("UseMiddleware<PrintBridgeAuthMiddleware>();", pipeline, StringComparison.Ordinal);
        var tenantResolution = pipeline.IndexOf("UseMiddleware<TenantResolutionMiddleware>();", StringComparison.Ordinal);
        var printBridge = pipeline.IndexOf("UseMiddleware<PrintBridgeAuthMiddleware>();", StringComparison.Ordinal);
        var sessionUnavailable = pipeline.IndexOf("UseMiddleware<TenantSessionUnavailableMiddleware>();", StringComparison.Ordinal);
        var authentication = pipeline.IndexOf("UseAuthentication();", StringComparison.Ordinal);
        Assert.True(tenantResolution >= 0 && tenantResolution < printBridge && printBridge < sessionUnavailable && sessionUnavailable < authentication);

        Assert.DoesNotContain("orderhub_auth", program, StringComparison.Ordinal);
        Assert.DoesNotContain("orderhub_auth", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieAuthenticationDefaults", program, StringComparison.Ordinal);
        Assert.DoesNotContain("SignInAsync", authController, StringComparison.Ordinal);
        Assert.Contains("WaslaAuthContracts.TenantIdClaim", authController, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidTenantCookie_ReachesProtectedEndpoint_WithTenantIdClaim()
    {
        var cookie = await IssueCookieAsync(
            WaslaAuthContracts.TenantScheme,
            WaslaAuthContracts.TenantCookieName,
            TenantPrincipal(_tenantId));

        using var host = await StartApiHostAsync(_tenantId);
        var response = await SendAsync(host, "/api/tenant", cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_tenantId.ToString(), await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingCookie_IsUnauthorized()
    {
        using var host = await StartApiHostAsync(_tenantId);
        var response = await SendAsync(host, "/api/tenant", cookieHeader: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantClaimForAnotherTenant_IsForbidden()
    {
        var otherTenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var cookie = await IssueCookieAsync(
            WaslaAuthContracts.TenantScheme,
            WaslaAuthContracts.TenantCookieName,
            TenantPrincipal(otherTenant));

        using var host = await StartApiHostAsync(_tenantId);
        var response = await SendAsync(host, "/api/tenant", cookie);

        // The role policy's own TenantId binding, behind session validation (which rejects such a session with 401).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CentralAdminCookie_IsNotTenantApiAuthentication()
    {
        var cookie = await IssueCookieAsync(
            WaslaAuthContracts.CentralAdminScheme,
            WaslaAuthContracts.CentralAdminCookieName,
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "CentralAdmin"),
                new Claim(WaslaAuthContracts.TenantIdClaim, _tenantId.ToString())
            ],
            WaslaAuthContracts.CentralAdminScheme)));

        using var host = await StartApiHostAsync(_tenantId);
        var response = await SendAsync(host, "/api/tenant", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LegacyOrderHubCookie_IsNotAuthenticated_AndIsExpired()
    {
        var cookie = await IssueCookieAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            WaslaAuthContracts.LegacyTenantCookieName,
            TenantPrincipal(_tenantId));

        using var host = await StartApiHostAsync(_tenantId);
        var response = await SendAsync(host, "/api/tenant", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var setCookie = string.Join("\n", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("orderhub_auth=", setCookie, StringComparison.Ordinal);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TenantCookie_DoesNotAuthenticateCentralAdminSurface()
    {
        var tenantCookie = await IssueCookieAsync(
            WaslaAuthContracts.TenantScheme,
            WaslaAuthContracts.TenantCookieName,
            TenantPrincipal(_tenantId));
        var adminCookie = await IssueCookieAsync(
            WaslaAuthContracts.CentralAdminScheme,
            WaslaAuthContracts.CentralAdminCookieName,
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "CentralAdmin")],
                WaslaAuthContracts.CentralAdminScheme)));

        await using var host = await StartIsolatedHostAsync();
        var tenantOnAdmin = await SendAsync(host, "/admin", tenantCookie);
        var adminOnTenant = await SendAsync(host, "/tenant", adminCookie);
        var tenantOnTenant = await SendAsync(host, "/tenant", tenantCookie);
        var adminOnAdmin = await SendAsync(host, "/admin", adminCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, tenantOnAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, adminOnTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tenantOnTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminOnAdmin.StatusCode);
    }

    private async Task<string> IssueCookieAsync(string scheme, string cookieName, ClaimsPrincipal principal)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddSharedDataProtection(builder.Services);
        builder.Services.AddAuthentication()
            .AddCookie(scheme, options =>
            {
                options.Cookie.Name = cookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });
        var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/issue", async http =>
        {
            await http.SignInAsync(scheme, principal);
            http.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var response = await client.GetAsync("/issue");
            response.EnsureSuccessStatusCode();
            return response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private async Task<WebApplication> StartApiHostAsync(Guid resolvedTenantId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddSharedDataProtection(builder.Services);
        builder.Services.AddSingleton<ICurrentTenantService>(new FixedTenant(resolvedTenantId));
        builder.Services.AddWaslaApiTenantAuthentication(CookieSecurePolicy.None);
        // These tests cover which cookie and scheme authenticate. Session revalidation against the tenant database is
        // covered over real HTTP by ApiTenantSessionTests; here every session the scheme reads is accepted.
        builder.Services.AddSingleton<ITenantSessionValidator>(new AcceptingSessionValidator());
        var app = builder.Build();
        app.UseMiddleware<ExpireLegacyTenantAuthCookieMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/tenant", (ClaimsPrincipal user) =>
            user.FindFirst(WaslaAuthContracts.TenantIdClaim)!.Value)
            .RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private async Task<WebApplication> StartIsolatedHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddSharedDataProtection(builder.Services);
        builder.Services.AddAuthentication()
            .AddCookie(WaslaAuthContracts.TenantScheme, options =>
            {
                options.Cookie.Name = WaslaAuthContracts.TenantCookieName;
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            })
            .AddCookie(WaslaAuthContracts.CentralAdminScheme, options =>
            {
                options.Cookie.Name = WaslaAuthContracts.CentralAdminCookieName;
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/tenant", () => Results.Ok())
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(WaslaAuthContracts.TenantScheme)
                .RequireAuthenticatedUser());
        app.MapGet("/admin", () => Results.Ok())
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(WaslaAuthContracts.CentralAdminScheme)
                .RequireAuthenticatedUser());
        await app.StartAsync();
        return app;
    }

    private void AddSharedDataProtection(IServiceCollection services)
    {
        Directory.CreateDirectory(_keyDirectory);
        services.AddDataProtection()
            .SetApplicationName("Wasla")
            .PersistKeysToFileSystem(new DirectoryInfo(_keyDirectory));
    }

    private static ClaimsPrincipal TenantPrincipal(Guid tenantId) =>
        new(new ClaimsIdentity(
        [
            new Claim(WaslaAuthContracts.TenantIdClaim, tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Owner"),
            new Claim("Role", "Owner")
        ],
        WaslaAuthContracts.TenantScheme));

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, string path, string? cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookieHeader is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return await client.SendAsync(request);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wasla.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Wasla.sln was not found.");
    }

    private sealed class FixedTenant(Guid tenantId) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } = new(tenantId, "Tenant", "tenant", "tenant.wasla.local");
    }

    private sealed class AcceptingSessionValidator : ITenantSessionValidator
    {
        public Task<TenantSessionState> ValidateAsync(Guid? resolvedTenantId, ClaimsPrincipal? principal, CancellationToken ct) =>
            Task.FromResult(TenantSessionState.Valid);
    }
}
