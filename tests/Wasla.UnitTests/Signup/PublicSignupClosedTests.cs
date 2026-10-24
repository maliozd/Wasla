using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Plans;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// <c>Wasla:TenantOnboarding:PublicSignupEnabled</c> (WAS-76). Without a payment provider a public signup cannot be
/// completed, so a deployment closes it: <c>/signup</c> explains that online registration is not open yet, a submission
/// creates nothing, and the form's address lookups answer 404, while existing registrations keep their status and
/// checkout pages. Open (the default, and Development) everything stays as before. Every test serves the real signup and
/// checkout controllers, views and rate-limit registration (<see cref="SignupWebHost"/>) over its own in-memory CentralDb;
/// all data is fake.
/// </summary>
public sealed class PublicSignupClosedTests : IAsyncLifetime
{
    private static readonly ResourceManager WebResources =
        new("Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-signup-closed-keys-").FullName;
    private SignupWebHost? _host;

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string, string> ClosedEnvironmentsAndCultures => new()
    {
        { Environments.Production, "tr-TR" },
        { Environments.Production, "en-US" },
        { Environments.Production, "ar-SA" },
        { Environments.Production, "ru-RU" },
        { Environments.Staging, "tr-TR" }
    };

    // --- Closed ----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ClosedEnvironmentsAndCultures))]
    public async Task SignupPage_WhenClosed_SaysOnlineRegistrationIsNotOpen_WithoutAFormOrPasswordField(string environment, string culture)
    {
        var host = await StartAsync(environment, publicSignupEnabled: false);
        using var browser = host.NewBrowser();

        foreach (var path in new[] { "/signup", $"/signup?plan={WaslaPlanCodes.Starter}" })
        {
            var page = await browser.GetAsync($"{path}{(path.Contains('?') ? '&' : '?')}culture={culture}&ui-culture={culture}");

            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Equal("text/html", page.ContentType);
            AssertUnavailablePage(page, culture);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task SignupSubmission_WhenClosed_CreatesNoRegistration_AndShowsTheSamePageWith404(string environment)
    {
        var host = await StartAsync(environment, publicSignupEnabled: false);
        var existing = await SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        // The signup page has no form now, so the token comes from the payment summary of an existing registration.
        using var browser = OwnerBrowser(host, existing);
        var token = await browser.GetAntiforgeryTokenAsync($"/checkout/review/{existing.Id}");
        var form = SignupForm(token);

        var response = await browser.PostFormAsync("/signup", form);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Null(response.Location);
        AssertUnavailablePage(response, "tr-TR");
        Assert.DoesNotContain(response.SetCookies, c => c.Name.Value == SignupRegistrationOwnership.CookieName);
        Assert.Equal(1, await RegistrationCountAsync());
        Assert.Equal(0, await RegistrationCountAsync(Value(form, "OwnerEmail")));
    }

    [Fact]
    public async Task SignupSubmission_WhenClosed_WithoutAnAntiforgeryToken_IsStillRejectedByTheAntiforgeryCheck()
    {
        var host = await StartAsync(Environments.Production, publicSignupEnabled: false);
        using var browser = host.NewBrowser();
        var form = SignupForm("not-a-valid-token");

        var response = await browser.PostFormAsync("/signup", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(0, await RegistrationCountAsync());
    }

    // The gate runs before the signup rate limit, so repeated closed submissions never get the 429 signup form back.
    [Fact]
    public async Task SignupSubmission_WhenClosed_NeverReachesTheRateLimitResponse_ThatRendersTheForm()
    {
        var host = await StartAsync(Environments.Production, publicSignupEnabled: false, signupLimit: 1);
        var existing = await SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var browser = OwnerBrowser(host, existing);
        var token = await browser.GetAntiforgeryTokenAsync($"/checkout/review/{existing.Id}");

        for (var i = 0; i < 3; i++)
        {
            var response = await browser.PostFormAsync("/signup", SignupForm(token));
            Assert.Equal(HttpStatusCode.NotFound, response.Status);
            AssertUnavailablePage(response, "tr-TR");
        }

        Assert.Equal(1, await RegistrationCountAsync());
    }

    [Theory]
    [InlineData("/signup/districts?cityId=1")]
    [InlineData("/signup/neighborhoods?districtId=1")]
    [InlineData("/signup/streets?neighborhoodId=1")]
    public async Task AddressLookups_ServeOnlyTheSignupForm_AndAreNotFoundWhenClosed(string path)
    {
        var host = await StartAsync(Environments.Production, publicSignupEnabled: false);
        using var browser = host.NewBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal(string.Empty, response.Body);
    }

    [Fact]
    public async Task ExistingRegistration_WhenClosed_KeepsItsStatusPage_SummaryAndCancellation()
    {
        var host = await StartAsync(Environments.Production, publicSignupEnabled: false);
        var existing = await SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var stranger = host.NewBrowser();
        using var owner = OwnerBrowser(host, existing);

        var publicStatus = await stranger.GetAsync($"/signup/pending/{existing.Id}");
        var ownerStatus = await owner.GetAsync($"/signup/pending/{existing.Id}");
        var review = await owner.GetAsync($"/checkout/review/{existing.Id}");
        var cancel = await owner.PostFormAsync(
            $"/checkout/cancel/{existing.Id}",
            [new("__RequestVerificationToken", TestBrowser.AntiforgeryTokenIn(review.Body))]);

        Assert.Equal(HttpStatusCode.OK, publicStatus.Status);
        Assert.Contains(existing.Entity.PrimaryDomain, publicStatus.Body);
        Assert.Equal(HttpStatusCode.OK, ownerStatus.Status);
        Assert.Contains($"href=\"/checkout/review/{existing.Id}\"", ownerStatus.Body);
        Assert.Equal(HttpStatusCode.OK, review.Status);
        Assert.Contains($"action=\"/checkout/cancel/{existing.Id}\"", review.Body);
        Assert.Equal(HttpStatusCode.Redirect, cancel.Status);
        Assert.Equal($"/checkout/cancelled/{existing.Id}", cancel.Location);
        Assert.Equal(PendingRegistrationStatus.Cancelled, await StatusAsync(existing.Id));
    }

    // --- Open ------------------------------------------------------------------------------------

    // Production with the option set to true, and Development and Production without the key (the default is open).
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", null)]
    [InlineData("Development", null)]
    public async Task Signup_WhenOpen_KeepsTheForm_TheLookups_AndCreatesTheRegistration(string environment, bool? publicSignupEnabled)
    {
        var host = await StartAsync(environment, publicSignupEnabled);
        using var browser = host.NewBrowser();

        var page = await browser.GetAsync("/signup");
        var districts = await browser.GetAsync("/signup/districts?cityId=1");
        var form = SignupForm(TestBrowser.AntiforgeryTokenIn(page.Body));
        var submitted = await browser.PostFormAsync("/signup", form);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("id=\"signupForm\"", page.Body);
        Assert.Contains("type=\"password\"", page.Body);
        Assert.DoesNotContain("data-signup-unavailable", page.Body);
        Assert.DoesNotContain(Text("Signup.UnavailableTitle", "tr-TR"), page.Body);
        Assert.Equal(HttpStatusCode.OK, districts.Status);
        Assert.Equal("application/json", districts.ContentType);
        Assert.Contains("Mitte", districts.Body);
        Assert.Equal(HttpStatusCode.Redirect, submitted.Status);
        Assert.StartsWith("/signup/pending/", submitted.Location);
        Assert.Contains(submitted.SetCookies, c => c.Name.Value == SignupRegistrationOwnership.CookieName);
        Assert.Equal(1, await RegistrationCountAsync(Value(form, "OwnerEmail")));
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static void AssertUnavailablePage(TestResponse page, string culture)
    {
        Assert.Contains("data-signup-unavailable", page.Body);
        Assert.Contains($">{Text("Signup.UnavailableTitle", culture)}</h1>", page.Body);
        Assert.Contains(Text("Signup.UnavailableBody", culture), page.Body);
        Assert.Contains($"href=\"{PublicSiteUrls.InstagramUrl}\"", page.Body);
        Assert.Contains(Text("Marketing.Cta.ContactInstagram", culture), page.Body);
        Assert.DoesNotContain("<form", page.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("type=\"password\"", page.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__RequestVerificationToken", page.Body);
        Assert.DoesNotContain("signup-form.js", page.Body);
    }

    private async Task<SignupWebHost> StartAsync(string environment, bool? publicSignupEnabled, int signupLimit = 1000)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["AuthenticationRateLimiting:SignupSubmission:PermitLimit"] = signupLimit.ToString(CultureInfo.InvariantCulture)
        };
        if (publicSignupEnabled is bool enabled)
            configuration["Wasla:TenantOnboarding:PublicSignupEnabled"] = enabled ? "true" : "false";

        _host = await SignupWebHost.StartAsync(_connection, _keyRing, environment, configuration: configuration);
        return _host;
    }

    private async Task<SentinelApplicant> SeedAsync(PendingRegistrationStatus status)
    {
        var applicant = new SentinelApplicant(status);
        await using var db = NewDb();
        db.PendingRegistrations.Add(applicant.Entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return applicant;
    }

    private static TestBrowser OwnerBrowser(SignupWebHost host, SentinelApplicant applicant)
    {
        // Mints the proof exactly as a successful signup on that host would have.
        var proof = new SignupRegistrationOwnership(
                host.Services.GetRequiredService<IDataProtectionProvider>(),
                host.Services.GetRequiredService<IWebHostEnvironment>())
            .CreateProof(applicant.Id, DateTimeOffset.UtcNow.AddDays(7));
        var browser = host.NewBrowser();
        browser.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, proof);
        return browser;
    }

    private async Task<int> RegistrationCountAsync(string? ownerEmail = null)
    {
        await using var db = NewDb();
        return await db.PendingRegistrations
            .CountAsync(r => ownerEmail == null || r.OwnerEmail == ownerEmail, TestContext.Current.CancellationToken);
    }

    private async Task<PendingRegistrationStatus> StatusAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.PendingRegistrations.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => r.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private CentralDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private static string Text(string key, string culture) =>
        HtmlEncoder.Default.Encode(WebResources.GetString(key, CultureInfo.GetCultureInfo(culture))
            ?? throw new InvalidOperationException($"Missing resource {key}"));

    private static string Value(IEnumerable<KeyValuePair<string, string>> form, string key) => form.Single(f => f.Key == key).Value;

    private static List<KeyValuePair<string, string>> SignupForm(string antiforgeryToken)
    {
        var token = SentinelApplicant.NewToken();
        return
        [
            new("__RequestVerificationToken", antiforgeryToken),
            new("PlanCode", WaslaPlanCodes.Starter),
            new("BillingPeriod", "Monthly"),
            new("BusinessName", $"Bizname{token}"),
            new("SelectedBusinessTypeCodes", "burger"),
            new("BusinessPhoneType", "Mobile"),
            new("BusinessPhone", SentinelApplicant.NewPhone()),
            new("Slug", $"s{token}"),
            new("Country", "Germany"),
            new("CityId", "1"),
            new("DistrictId", "1"),
            new("City", "Berlin"),
            new("District", "Mitte"),
            new("StreetAddress", $"Streetq{token}"),
            new("OwnerFullName", $"Ownerq{token}"),
            new("OwnerEmail", $"owner.{token}@sentinel.test"),
            new("Password", $"Secret-pass-{token}"),
            new("ConfirmPassword", $"Secret-pass-{token}")
        ];
    }
}
