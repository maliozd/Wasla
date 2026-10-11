using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Plans;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Abuse limits on anonymous signup (WAS-90): the per-network limit on <c>POST /signup</c> and on the checkout POSTs that
/// change a registration, and the cap on active registrations per owner email. Every test serves the real signup and
/// checkout controllers, views and production rate-limit registration (<see cref="SignupWebHost"/>) over its own
/// in-memory CentralDb with its own limits. The client connects from 127.0.0.1; all data is fake.
/// </summary>
public sealed class SignupAbuseProtectionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-signup-limit-keys-").FullName;
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

    private async Task<SignupWebHost> StartAsync(int signupLimit = 1000, int checkoutLimit = 1000, string environment = "Production")
    {
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, environment, configuration: new Dictionary<string, string?>
        {
            ["AuthenticationRateLimiting:SignupSubmission:PermitLimit"] = signupLimit.ToString(CultureInfo.InvariantCulture),
            ["AuthenticationRateLimiting:SignupCheckout:PermitLimit"] = checkoutLimit.ToString(CultureInfo.InvariantCulture)
        });
        return _host;
    }

    // --- Per-network limit ------------------------------------------------------------------------

    [Fact]
    public async Task SignupOverTheNetworkLimit_Gets429WithTheFormAgain_AndCreatesNoRegistration()
    {
        var host = await StartAsync(signupLimit: 2);
        using var browser = host.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup");

        for (var i = 0; i < 2; i++)
        {
            var accepted = await browser.PostFormAsync("/signup", SignupForm(token));
            Assert.Equal(HttpStatusCode.Redirect, accepted.Status);
            Assert.StartsWith("/signup/pending/", accepted.Location);
        }

        Assert.Equal(2, await RegistrationCountAsync());
        var form = SignupForm(token);

        var limited = await browser.PostFormAsync("/signup", form);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Null(limited.Location);
        Assert.InRange(limited.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        Assert.Equal(2, await RegistrationCountAsync());
        Assert.DoesNotContain(limited.SetCookies, cookie => cookie.Name.Value == SignupRegistrationOwnership.CookieName);
        var html = WebUtility.HtmlDecode(limited.Body);
        Assert.Contains(Localized(host, "tr-TR", "Signup.TooManyAttempts"), html, StringComparison.Ordinal);
        Assert.Contains(Value(form, "BusinessName"), html, StringComparison.Ordinal);
        Assert.Contains(Value(form, "OwnerEmail"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Value(form, "Password"), html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
    }

    public static TheoryData<string> Cultures => ["tr-TR", "en-US", "ar-SA", "ru-RU"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task SignupLimitMessage_IsLocalized_AndArabicIsRightToLeft(string culture)
    {
        var host = await StartAsync(signupLimit: 1);
        using var browser = host.NewBrowser();
        browser.SetCookie(SignupWebHost.CentralHost, CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        var token = await browser.GetAntiforgeryTokenAsync("/signup");
        await browser.PostFormAsync("/signup", SignupForm(token));

        var limited = await browser.PostFormAsync("/signup", SignupForm(token));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        var html = WebUtility.HtmlDecode(limited.Body);
        Assert.Contains($"lang=\"{culture}\" dir=\"{(culture == "ar-SA" ? "rtl" : "ltr")}\"", html, StringComparison.Ordinal);
        Assert.Contains(Localized(host, culture, "Signup.TooManyAttempts"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Antiforgery_RunsBeforeTheLimit_AndARejectedTokenUsesNoPermit()
    {
        var host = await StartAsync(signupLimit: 1);
        using var browser = host.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup");

        var withoutToken = await browser.PostFormAsync("/signup", SignupForm(token).Where(f => f.Key != "__RequestVerificationToken"));
        var accepted = await browser.PostFormAsync("/signup", SignupForm(token));
        var limitedWithoutToken = await browser.PostFormAsync("/signup", SignupForm(token).Where(f => f.Key != "__RequestVerificationToken"));

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.Status);
        Assert.Equal(HttpStatusCode.Redirect, accepted.Status);
        Assert.Equal(HttpStatusCode.BadRequest, limitedWithoutToken.Status);
        Assert.Equal(1, await RegistrationCountAsync());
    }

    // --- Per-email cap --------------------------------------------------------------------------------

    [Fact]
    public async Task OneOwnerEmail_GetsAtMostThreeActiveRegistrations_WithTheSameAnswerAsTheNetworkLimit()
    {
        var host = await StartAsync();
        using var browser = host.NewBrowser();
        var token = await browser.GetAntiforgeryTokenAsync("/signup");
        var email = $"owner.{SentinelApplicant.NewToken()}@sentinel.test";

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync("/signup", SignupForm(token, email))).Status);

        // Upper case does not make it another email.
        var refused = await browser.PostFormAsync("/signup", SignupForm(token, email.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Contains(Localized(host, "tr-TR", "Signup.TooManyAttempts"), WebUtility.HtmlDecode(refused.Body), StringComparison.Ordinal);
        Assert.Equal(3, await RegistrationCountAsync(email));

        // The cap counts active registrations only: once one is cancelled, the email may sign up again.
        await using (var db = NewDb())
        {
            var one = await db.PendingRegistrations.FirstAsync(r => r.OwnerEmail == email, TestContext.Current.CancellationToken);
            one.Status = PendingRegistrationStatus.Cancelled;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync("/signup", SignupForm(token, email))).Status);
        Assert.Equal(4, await RegistrationCountAsync(email));
    }

    // --- Checkout -------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckoutChangesOverTheLimit_Get429_AndLeaveTheRegistrationUnchanged()
    {
        var host = await StartAsync(checkoutLimit: 1);
        var applicant = new SentinelApplicant(PendingRegistrationStatus.AwaitingPayment);
        await using (var db = NewDb())
        {
            db.PendingRegistrations.Add(applicant.Entity);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var stranger = host.NewBrowser();
        var strangerToken = await stranger.GetAntiforgeryTokenAsync("/signup");
        using var owner = host.NewBrowser();
        owner.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, ProofFor(host, applicant.Id));
        var review = await owner.GetAsync($"/checkout/review/{applicant.Id}");
        Assert.Equal(HttpStatusCode.OK, review.Status);

        // A stranger's attempt is refused as before (404) and uses the network's one permit.
        var strangerCancel = await stranger.PostFormAsync($"/checkout/cancel/{applicant.Id}", [new("__RequestVerificationToken", strangerToken)]);
        var ownerCancel = await owner.PostFormAsync($"/checkout/cancel/{applicant.Id}",
            [new("__RequestVerificationToken", TestBrowser.AntiforgeryTokenIn(review.Body))]);

        Assert.Equal(HttpStatusCode.NotFound, strangerCancel.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, ownerCancel.Status);
        Assert.NotNull(ownerCancel.RetryAfter);
        var html = WebUtility.HtmlDecode(ownerCancel.Body);
        Assert.Contains(Localized(host, "tr-TR", "Checkout.TooManyAttempts"), html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/checkout/review/{applicant.Id}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(applicant.Entity.OwnerEmail, html, StringComparison.Ordinal);
        await using var check = NewDb();
        var stored = await check.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Id == applicant.Id, TestContext.Current.CancellationToken);
        Assert.Equal(PendingRegistrationStatus.AwaitingPayment, stored.Status);
    }

    [Fact]
    public async Task PaymentSimulatorOverTheLimit_DoesNotMarkTheRegistrationPaid()
    {
        var host = await StartAsync(checkoutLimit: 1, environment: Environments.Development);
        var applicant = new SentinelApplicant(PendingRegistrationStatus.AwaitingPayment);
        await using (var db = NewDb())
        {
            db.PendingRegistrations.Add(applicant.Entity);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var owner = host.NewBrowser();
        owner.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, ProofFor(host, applicant.Id));
        var token = TestBrowser.AntiforgeryTokenIn((await owner.GetAsync($"/checkout/review/{applicant.Id}")).Body);
        var failed = await owner.PostFormAsync($"/checkout/simulate-failed/{applicant.Id}", [new("__RequestVerificationToken", token)]);

        var success = await owner.PostFormAsync($"/checkout/simulate-success/{applicant.Id}", [new("__RequestVerificationToken", token)]);

        Assert.Equal(HttpStatusCode.Redirect, failed.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, success.Status);
        await using var check = NewDb();
        var stored = await check.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Id == applicant.Id, TestContext.Current.CancellationToken);
        Assert.Equal(PendingRegistrationStatus.PaymentFailed, stored.Status);
        Assert.Null(stored.PaymentSucceededAtUtc);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static List<KeyValuePair<string, string>> SignupForm(string antiforgeryToken, string? ownerEmail = null)
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
            new("OwnerEmail", ownerEmail ?? $"owner.{token}@sentinel.test"),
            new("Password", $"Secret-pass-{token}"),
            new("ConfirmPassword", $"Secret-pass-{token}")
        ];
    }

    private static string Value(IEnumerable<KeyValuePair<string, string>> form, string key) => form.Single(f => f.Key == key).Value;

    private async Task<int> RegistrationCountAsync(string? ownerEmail = null)
    {
        await using var db = NewDb();
        return ownerEmail is null
            ? await db.PendingRegistrations.CountAsync(TestContext.Current.CancellationToken)
            : await db.PendingRegistrations.CountAsync(r => r.OwnerEmail.ToLower() == ownerEmail.ToLower(), TestContext.Current.CancellationToken);
    }

    private CentralDbContext NewDb() => new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private static string ProofFor(SignupWebHost host, Guid registrationId) =>
        new SignupRegistrationOwnership(
                host.Services.GetRequiredService<IDataProtectionProvider>(),
                host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>())
            .CreateProof(registrationId, DateTimeOffset.UtcNow.AddDays(7));

    private static string Localized(SignupWebHost host, string culture, string key)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var value = host.Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key];
            Assert.False(value.ResourceNotFound, $"{key} has no {culture} resource.");
            return value.Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
