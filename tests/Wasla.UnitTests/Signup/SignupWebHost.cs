using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Abstractions.Sms;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Application.Abstractions.Billing;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sms;
using Wasla.Infrastructure.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Middleware;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Serves the real signup/checkout controllers, <see cref="TenantResolutionMiddleware"/>, and the
/// compiled Razor views of Wasla.Web over loopback HTTP, backed by an isolated SQLite CentralDb.
/// </summary>
/// <remarks>
/// It mirrors the parts of Program.cs these routes depend on (localization, antiforgery, Data
/// Protection with application name "Wasla" and a file-system key ring, the tenant-resolution
/// middleware, and MVC routing). It does not run the whole Program.cs composition.
/// </remarks>
internal sealed class SignupWebHost : IAsyncDisposable
{
    public const string CentralHost = "wasla.local";

    /// <summary>The configuration that publishes the full marketing site instead of the coming-soon page.</summary>
    public static readonly IReadOnlyDictionary<string, string?> FullMarketingSite = new Dictionary<string, string?>
    {
        [MarketingSiteOptions.SectionName + ":" + nameof(MarketingSiteOptions.FullSiteEnabled)] = "true"
    };

    private readonly WebApplication _app;
    private readonly string _contentRoot;

    private SignupWebHost(WebApplication app, string contentRoot, Uri baseAddress)
    {
        _app = app;
        _contentRoot = contentRoot;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>The fake SMS outbox (WAS-192); null for a host started with <c>fakeSms: false</c>.</summary>
    public FakeSmsOutbox? FakeSms => _app.Services.GetService<FakeSmsOutbox>();

    /// <summary>
    /// An antiforgery request token for <paramref name="browser"/>, with its cookie stored in the browser's jar, made by
    /// the host's own antiforgery service: JSON API tests do not depend on how a page renders the token.
    /// </summary>
    public string IssueAntiforgeryToken(TestBrowser browser, string host = CentralHost)
    {
        var antiforgery = _app.Services.GetRequiredService<IAntiforgery>();
        var options = _app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var tokens = antiforgery.GetTokens(new DefaultHttpContext { RequestServices = _app.Services });
        browser.SetCookie(host, options.Cookie.Name!, tokens.CookieToken!);
        return tokens.RequestToken!;
    }

    public static async Task<SignupWebHost> StartAsync(
        SqliteConnection centralDb,
        string keyRingPath,
        string environmentName = "Production",
        string applicationName = "Wasla",
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null,
        bool fakeSms = true)
    {
        var contentRoot = Directory.CreateTempSubdirectory("wasla-signup-host-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ApplicationName = typeof(SignupController).Assembly.GetName().Name,
            ContentRootPath = contentRoot
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddMemoryCache();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(options =>
        {
            var supported = new[] { "tr-TR", "en-US", "ar-SA", "ru-RU" }
                .Select(CultureInfo.GetCultureInfo)
                .ToList();
            options.DefaultRequestCulture = new RequestCulture("tr-TR");
            options.SupportedCultures = supported;
            options.SupportedUICultures = supported;
        });

        // Same Data Protection shape as Program.cs: application name "Wasla", keys in DataProtection:KeyPath.
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        services.AddAntiforgery(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        });
        services.AddControllersWithViews()
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix);

        services.AddDbContext<CentralDbContext>(options => options.UseSqlite(centralDb));
        // Wasla:TenantOnboarding from the test's configuration, otherwise the Development domains (WAS-66).
        services.Configure<TenantOnboardingOptions>(builder.Configuration.GetSection(TenantOnboardingOptions.SectionName));
        // Wasla:MarketingSite as Program.cs binds it: without the test's configuration only the coming-soon page is published.
        services.Configure<MarketingSiteOptions>(builder.Configuration.GetSection(MarketingSiteOptions.SectionName));
        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddScoped<ISignupReferenceDataService, SignupReferenceDataService>();
        services.AddScoped<IPendingRegistrationService, PendingRegistrationService>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IBillingPlanPriceReader, BillingPlanPriceReader>();
        services.AddScoped<ITenantResolver, TenantResolver>();

        // The signup/checkout limits and the unknown-host cache Program.cs registers. A class shares one host and posts
        // from one loopback address, so the limits are raised unless a test passes its own; SignupRateLimitTests covers them.
        builder.Configuration["AuthenticationRateLimiting:SignupSubmission:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:SignupCheckout:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:ContactSubmission:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:SignupDraftSave:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:SignupDraftCreation:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:SignupPhoneSend:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:SignupPhoneVerify:PermitLimit"] = "1000";
        builder.Configuration["AuthenticationRateLimiting:SignupAddressAvailability:PermitLimit"] = "1000";
        // The public contact page Program.cs registers; Email:Provider defaults to Log, so nothing is sent.
        builder.Configuration["Wasla:Contact:RecipientEmail"] = "contact-inbox@wasla.test";
        if (configuration is not null)
            builder.Configuration.AddInMemoryCollection(configuration);
        services.AddWaslaWebAuthenticationRateLimiting(builder.Configuration);
        services.AddWaslaEmail(builder.Configuration);
        services.AddWaslaContactMessages(builder.Configuration);
        services.AddWaslaTenantHostMissCache(builder.Configuration);
        services.AddWaslaHealthChecks();
        // Registration drafts and phone verification as Program.cs registers them (Wasla:Signup, Wasla:Sms:Mode). A test
        // host registers the fake sender itself, in any environment, unless it tests the sender modes (fakeSms: false).
        services.AddWaslaRegistrationDrafts(builder.Configuration, builder.Environment);
        if (fakeSms)
            UseFakeSms(services);
        configureServices?.Invoke(services);

        var app = builder.Build();
        app.UseRequestLocalization();
        app.UseRouting();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.MapControllers();
        app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
        app.MapWaslaHealthChecks();

        await app.StartAsync();
        return new SignupWebHost(app, contentRoot, new Uri(app.Urls.First()));
    }

    public TestBrowser NewBrowser() => new(BaseAddress);

    /// <summary>Replaces the configured SMS sender with the fake sender and its outbox, which tests read through DI.</summary>
    public static void UseFakeSms(IServiceCollection services)
    {
        services.RemoveAll<ISmsSender>();
        services.RemoveAll<FakeSmsOutbox>();
        services.AddSingleton<FakeSmsOutbox>();
        services.AddSingleton<ISmsSender, FakeSmsSender>();
    }

    /// <summary>
    /// Creates the isolated CentralDb schema with the one city/district the signup form posts
    /// (CityId/DistrictId are required fields and foreign keys).
    /// </summary>
    public static async Task CreateCentralDbAsync(SqliteConnection connection)
    {
        await using var db = new CentralDbContext(
            new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Cities.Add(new Wasla.Domain.Entities.Central.City { Id = 1, CountryCode = "DE", Name = "Berlin", IsActive = true });
        db.Districts.Add(new Wasla.Domain.Entities.Central.District { Id = 1, CityId = 1, Name = "Mitte", IsActive = true });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// A browser with its own cookie jar. Cookies without a Domain attribute are host-only, so they are
/// stored and sent per host exactly as a real browser scopes them.
/// </summary>
internal sealed class TestBrowser : IDisposable
{
    private static readonly Regex AntiforgeryField = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"",
        RegexOptions.Compiled);

    private readonly HttpClient _client;
    private readonly Dictionary<string, Dictionary<string, string>> _jar = new(StringComparer.OrdinalIgnoreCase);

    public TestBrowser(Uri baseAddress)
    {
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = baseAddress
        };
    }

    public void SetCookie(string host, string name, string value)
    {
        if (!_jar.TryGetValue(host, out var cookies))
            _jar[host] = cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        cookies[name] = value;
    }

    public string? GetCookie(string host, string name) =>
        _jar.TryGetValue(host, out var cookies) && cookies.TryGetValue(name, out var value) ? value : null;

    public Task<TestResponse> GetAsync(string path, string host = SignupWebHost.CentralHost) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), host);

    /// <summary>A GET with extra request headers, for example forwarded headers a client sends itself.</summary>
    public Task<TestResponse> SendWithHeadersAsync(string path, string host, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return SendAsync(request, host);
    }

    public Task<TestResponse> PostFormAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>> fields,
        string host = SignupWebHost.CentralHost) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) }, host);

    /// <summary>A JSON request with the antiforgery header, as the signup page's script sends it.</summary>
    public Task<TestResponse> SendJsonAsync(
        HttpMethod method,
        string path,
        object? body,
        string? antiforgeryToken,
        string host = SignupWebHost.CentralHost)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType());
        if (antiforgeryToken is not null)
            request.Headers.TryAddWithoutValidation("RequestVerificationToken", antiforgeryToken);
        return SendAsync(request, host);
    }

    public void RemoveCookie(string host, string name)
    {
        if (_jar.TryGetValue(host, out var cookies))
            cookies.Remove(name);
    }

    /// <summary>Loads a page and returns the antiforgery token from its first form.</summary>
    public async Task<string> GetAntiforgeryTokenAsync(string path, string host = SignupWebHost.CentralHost)
    {
        var page = await GetAsync(path, host);
        Assert.Equal(HttpStatusCode.OK, page.Status);
        return AntiforgeryTokenIn(page.Body);
    }

    public static string AntiforgeryTokenIn(string html)
    {
        var match = AntiforgeryField.Match(html);
        Assert.True(match.Success, "The page has no antiforgery form field.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private async Task<TestResponse> SendAsync(HttpRequestMessage request, string host)
    {
        request.Headers.Host = host;
        if (_jar.TryGetValue(host, out var cookies) && cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var setCookies = response.Headers.TryGetValues(HeaderNames.SetCookie, out var values)
            ? SetCookieHeaderValue.ParseList(values.ToList())
            : new List<SetCookieHeaderValue>();
        foreach (var cookie in setCookies.Where(c => string.IsNullOrEmpty(c.Domain.Value)))
        {
            // A deletion (empty value or an expiry in the past) removes the cookie, as a browser does.
            if (string.IsNullOrEmpty(cookie.Value.Value) || cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow)
                RemoveCookie(host, cookie.Name.Value!);
            else
                SetCookie(host, cookie.Name.Value!, cookie.Value.Value!);
        }

        return new TestResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            response.Headers.Location?.OriginalString,
            setCookies.ToList(),
            response.Headers.CacheControl?.ToString(),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.RetryAfter?.Delta);
    }

    public void Dispose() => _client.Dispose();
}

internal sealed record TestResponse(
    HttpStatusCode Status,
    string Body,
    string? Location,
    IReadOnlyList<SetCookieHeaderValue> SetCookies,
    string? CacheControl,
    string? ContentType,
    TimeSpan? RetryAfter = null)
{
    /// <summary>The JSON body.</summary>
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}
