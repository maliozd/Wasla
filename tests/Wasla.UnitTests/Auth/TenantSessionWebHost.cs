using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.Web;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Middleware;
using Wasla.Web.Security;
using CurrentTenantService = Wasla.Web.Tenant.CurrentTenantService;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Serves the tenant application in-process for tenant session tests: the real <see cref="TenantResolutionMiddleware"/>
/// resolving two tenant hosts (<c>alpha.wasla.local</c>, <c>beta.wasla.local</c>) from a SQLite CentralDb, the real tenant
/// cookie scheme, the tenant role policies read from Web Program.cs, the real tenant AuthController and
/// TenantUsersController with their compiled views, and the real Infrastructure services over one SQLite database per
/// tenant. Requests carry a tenant host in the Host header. Users, passwords and data are fake.
/// </summary>
internal sealed class TenantSessionWebHost : IAsyncDisposable
{
    public const string Password = "Correct-Horse-9";
    public const string AlphaHost = "alpha.wasla.local";
    public const string BetaHost = "beta.wasla.local";

    private readonly WebApplication _app;
    private readonly string _contentRoot;
    private readonly CentralTestDatabase _central;

    private TenantSessionWebHost(
        WebApplication app,
        string contentRoot,
        CentralTestDatabase central,
        RecordingTenantDbFactory tenants,
        Guid alphaId,
        Guid betaId,
        CapturingLoggerProvider logs)
    {
        _app = app;
        _contentRoot = contentRoot;
        _central = central;
        Tenants = tenants;
        AlphaId = alphaId;
        BetaId = betaId;
        Logs = logs;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    public Guid AlphaId { get; }

    public Guid BetaId { get; }

    public RecordingTenantDbFactory Tenants { get; }

    public CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TenantSessionWebHost> StartAsync()
    {
        var central = new CentralTestDatabase();
        var tenants = new RecordingTenantDbFactory();
        var alpha = central.AddTenant("alpha");
        var beta = central.AddTenant("beta");
        tenants.CreateTenantDatabase(alpha.Id);
        tenants.CreateTenantDatabase(beta.Id);

        var contentRoot = Directory.CreateTempSubdirectory("wasla-session-host-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(SharedResource).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            WebRootPath = TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "wwwroot")
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
            ["ConnectionStrings:CentralDb"] = "Server=(none);Database=WaslaSessionTests"
        });

        var services = builder.Services;
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(options =>
        {
            var supported = new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" }.Select(CultureInfo.GetCultureInfo).ToList();
            options.DefaultRequestCulture = new RequestCulture("en-US");
            options.SupportedCultures = supported;
            options.SupportedUICultures = supported;
            options.RequestCultureProviders = new List<IRequestCultureProvider> { new CookieRequestCultureProvider() };
        });
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = AuthSchemes.Tenant;
                options.DefaultAuthenticateScheme = AuthSchemes.Tenant;
                options.DefaultChallengeScheme = AuthSchemes.Tenant;
            })
            // The tenant cookie registration Web Program.cs uses, including its session revalidation.
            .AddWaslaTenantCookie(CookieSecurePolicy.SameAsRequest)
            .AddCookie(AuthSchemes.CentralAdmin, options => options.Cookie.Name = CentralAdminAuthCookieNames.Active);

        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        services.AddScoped<ITenantNavigationAuthorizationService, TenantNavigationAuthorizationService>();
        services.AddScoped<IGuidedSetupCoordinator, GuidedSetupCoordinator>();
        services.AddAuthorization(ProgramPolicies.Register);
        services.AddControllersWithViews()
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization(options =>
                options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

        // The real application services, with both databases replaced by SQLite.
        services.AddWaslaInfrastructure(builder.Configuration);
        services.RemoveAll<DbContextOptions<CentralDbContext>>();
        services.RemoveAll<CentralDbContext>();
        services.AddDbContext<CentralDbContext>(options => options.UseSqlite(central.ConnectionString));
        services.RemoveAll<ITenantDbContextFactory>();
        services.AddSingleton<ITenantDbContextFactory>(tenants);

        var app = builder.Build();

        // Same middleware order as Web Program.cs.
        app.UseWaslaExceptionHandling(app.Environment);
        app.UseRequestLocalization();
        app.UseRouting();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapControllerRoute(name: "areas", pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");
        app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

        await app.StartAsync();
        return new TenantSessionWebHost(app, contentRoot, central, tenants, alpha.Id, beta.Id, logs);
    }

    public TenantSessionClient Client(string host) => new(BaseAddress, host);

    public AppUser SeedUser(Guid tenantId, string email, UserRole role, bool isActive = true, Guid? id = null)
    {
        var user = new AppUser
        {
            Id = id ?? Guid.NewGuid(),
            Email = email,
            // Low work factor: fake test credentials only.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4),
            FullName = email.Split('@')[0],
            Role = role,
            IsActive = isActive
        };
        Tenants.Seed(tenantId, db => db.AppUsers.Add(user));
        return user;
    }

    public AppUser? ReadUser(Guid tenantId, Guid userId)
    {
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(Tenants.ConnectionStringFor(tenantId)).Options);
        return db.AppUsers.AsNoTracking().SingleOrDefault(u => u.Id == userId);
    }

    public void UpdateUser(Guid tenantId, Guid userId, Action<AppUser> change)
    {
        using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(Tenants.ConnectionStringFor(tenantId)).Options);
        var user = db.AppUsers.Single(u => u.Id == userId);
        change(user);
        db.SaveChanges();
    }

    public void DeleteUser(Guid tenantId, Guid userId) =>
        Tenants.Execute(tenantId, $"DELETE FROM \"AppUsers\" WHERE \"Id\" = '{userId.ToString().ToUpperInvariant()}'");

    /// <summary>
    /// Builds a tenant auth cookie value the way the cookie handler does (the scheme's own ticket format and
    /// Data Protection keys), for principals the login flow does not produce: missing or malformed claims, an old
    /// issue time. The real handler reads it back on the next request.
    /// </summary>
    public string ProtectTenantCookie(IEnumerable<Claim> claims, DateTimeOffset issuedUtc, DateTimeOffset expiresUtc)
    {
        var options = _app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthSchemes.Tenant);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Tenant)),
            new AuthenticationProperties { IssuedUtc = issuedUtc, ExpiresUtc = expiresUtc },
            AuthSchemes.Tenant);
        return options.TicketDataFormat.Protect(ticket);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Tenants.Dispose();
        _central.Dispose();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>The tenant role policies exactly as Web Program.cs registers them, read from that file.</summary>
    private static class ProgramPolicies
    {
        public static void Register(AuthorizationOptions options)
        {
            var program = File.ReadAllText(TenantOperationsRulesTests.RepoFile("src", "Wasla.Web", "Program.cs"));
            var registrations = Regex.Matches(program, @"options\.AddTenantRolePolicy\(TenantPolicies\.(\w+),\s*((?:UserRole\.\w+(?:,\s*)?)+)\);");
            if (registrations.Count < 11)
                throw new InvalidOperationException("The Program.cs tenant policy table was not found.");

            foreach (Match registration in registrations)
            {
                var roles = Regex.Matches(registration.Groups[2].Value, @"UserRole\.(\w+)")
                    .Select(m => Enum.Parse<UserRole>(m.Groups[1].Value))
                    .ToArray();
                options.AddPolicy(registration.Groups[1].Value, policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.Requirements.Add(new TenantRoleRequirement(roles));
                });
            }
        }
    }
}

/// <summary>
/// A browser-like client for one Host header: it keeps its own cookies (including deletions), never follows
/// redirects, and reads antiforgery tokens from rendered forms.
/// </summary>
internal sealed class TenantSessionClient : IDisposable
{
    private static readonly Regex AntiforgeryInput = new(
        "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.Compiled);

    private readonly HttpClient _http;

    public TenantSessionClient(Uri baseAddress, string host)
    {
        Host = host;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = baseAddress
        };
    }

    public string Host { get; }

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    public string? AuthCookie => Cookies.GetValueOrDefault(TenantAuthCookieNames.Active);

    /// <summary>Every Set-Cookie header of the last response, as sent.</summary>
    public IReadOnlyList<string> LastSetCookies { get; private set; } = [];

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> form) =>
        SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form));

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = Host;
        if (Cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));

        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        foreach (var header in LastSetCookies)
        {
            var cookie = SetCookieHeaderValue.Parse(header);
            var name = cookie.Name.ToString();
            if (cookie.Expires is { } expires && expires <= DateTimeOffset.UtcNow || cookie.Value.Length == 0)
                Cookies.Remove(name);
            else
                Cookies[name] = cookie.Value.ToString();
        }

        return response;
    }

    /// <summary>Signs in through the real login form; the response must be the post-login redirect.</summary>
    public async Task LoginAsync(string email, string password = TenantSessionWebHost.Password)
    {
        var token = await AntiforgeryTokenAsync("/auth/login");
        var response = await PostFormAsync("/auth/login",
        [
            new("Email", email),
            new("Password", password),
            new("RememberMe", "false"),
            new("__RequestVerificationToken", token)
        ]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.OriginalString);
        Assert.NotNull(AuthCookie);
    }

    public async Task<string> AntiforgeryTokenAsync(string path)
    {
        var response = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var match = AntiforgeryInput.Match(html);
        Assert.True(match.Success, $"No antiforgery token on {path}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>The same cookies, sent to another host: a manual replay, which a browser would not do.</summary>
    public TenantSessionClient ReplayTo(Uri baseAddress, string host)
    {
        var replay = new TenantSessionClient(baseAddress, host);
        foreach (var cookie in Cookies)
            replay.Cookies[cookie.Key] = cookie.Value;
        return replay;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
