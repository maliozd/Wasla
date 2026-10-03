using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Plans;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Middleware;

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

    public static async Task<SignupWebHost> StartAsync(
        SqliteConnection centralDb,
        string keyRingPath,
        string environmentName = "Production",
        string applicationName = "Wasla")
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
        services.Configure<CustomerOnboardingOptions>(_ => { });
        services.AddSingleton<IWaslaPlanCatalog, WaslaPlanCatalog>();
        services.AddScoped<ISignupReferenceDataService, SignupReferenceDataService>();
        services.AddScoped<IPendingRegistrationService, PendingRegistrationService>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        // The signup validator is not under test here; the controller's own checks still run.
        services.AddSingleton<IValidator<PendingRegistrationRequest>>(new InlineValidator<PendingRegistrationRequest>());

        var app = builder.Build();
        app.UseRequestLocalization();
        app.UseRouting();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.MapControllers();
        app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

        await app.StartAsync();
        return new SignupWebHost(app, contentRoot, new Uri(app.Urls.First()));
    }

    public TestBrowser NewBrowser() => new(BaseAddress);

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

    public Task<TestResponse> PostFormAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>> fields,
        string host = SignupWebHost.CentralHost) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) }, host);

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
            SetCookie(host, cookie.Name.Value!, cookie.Value.Value!);

        return new TestResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            response.Headers.Location?.OriginalString,
            setCookies.ToList(),
            response.Headers.CacheControl?.ToString(),
            response.Content.Headers.ContentType?.MediaType);
    }

    public void Dispose() => _client.Dispose();
}

internal sealed record TestResponse(
    HttpStatusCode Status,
    string Body,
    string? Location,
    IReadOnlyList<SetCookieHeaderValue> SetCookies,
    string? CacheControl,
    string? ContentType);
