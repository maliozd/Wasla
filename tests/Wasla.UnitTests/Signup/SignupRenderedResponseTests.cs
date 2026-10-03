using System.Net;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Wasla.Application.Abstractions.Plans;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Formatting;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// Requests the real rendered HTML of the signup/checkout pages. A registration ID is discoverable
/// (pending-tenant redirect, central host lookup), so a browser holding only the ID must never receive
/// the applicant's details, while the browser that submitted the signup still sees them.
/// </summary>
public sealed class SignupRenderedResponseTests : IClassFixture<SignupRenderedResponseTests.Hosts>
{
    private readonly Hosts _hosts;

    public SignupRenderedResponseTests(Hosts hosts)
    {
        _hosts = hosts;
    }

    public static TheoryData<PendingRegistrationStatus> AllStatuses => new()
    {
        PendingRegistrationStatus.AwaitingPayment,
        PendingRegistrationStatus.PaymentFailed,
        PendingRegistrationStatus.PaymentSucceeded,
        PendingRegistrationStatus.Provisioned,
        PendingRegistrationStatus.Cancelled,
        PendingRegistrationStatus.Expired
    };

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task PendingPage_ShowsDetailsOnlyToTheApplicantsBrowser(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var stranger = _hosts.Production.NewBrowser();
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var strangerPage = await stranger.GetAsync($"/signup/pending/{applicant.Id}");
        var ownerPage = await owner.GetAsync($"/signup/pending/{applicant.Id}");

        AssertHtml(strangerPage);
        AssertNoPrivateData(strangerPage, applicant);
        Assert.Contains(applicant.Entity.PrimaryDomain, strangerPage.Body);
        Assert.DoesNotContain($"/checkout/review/{applicant.Id}", strangerPage.Body);

        AssertHtml(ownerPage);
        Assert.Contains(Html(applicant.Entity.OwnerFullName), ownerPage.Body);
        Assert.Contains(Html(applicant.Entity.OwnerEmail), ownerPage.Body);
        Assert.Contains(Html(applicant.Entity.BusinessName), ownerPage.Body);
        Assert.Contains(Html(applicant.Entity.BusinessEmail!), ownerPage.Body);
        Assert.Contains(Html(TurkishPhoneDisplayFormatter.FormatWithCountryCode(applicant.Entity.OwnerPhone)), ownerPage.Body);
        Assert.Contains(Html(TurkishPhoneDisplayFormatter.FormatWithCountryCode(applicant.Entity.BusinessPhone)), ownerPage.Body);
        Assert.Equal(
            status == PendingRegistrationStatus.AwaitingPayment,
            ownerPage.Body.Contains($"/checkout/review/{applicant.Id}", StringComparison.Ordinal));

        AssertNoProofIssued(strangerPage);
        AssertNoProofIssued(ownerPage);
        AssertNotStoredByCaches(ownerPage);
        AssertNotStoredByCaches(strangerPage);
    }

    // The payment simulator exists only in Development, so no other page may promise a simulated payment.
    [Fact]
    public async Task PendingPage_MentionsTheSimulatedPaymentOnlyToTheApplicantInDevelopment()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        var note = Html(new System.Resources.ResourceManager(
                "Wasla.Web.Resources.SharedResource", typeof(Wasla.Web.SharedResource).Assembly)
            .GetString("Signup.PendingTestNote", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))!);
        using var productionOwner = _hosts.OwnerBrowser(_hosts.Production, applicant);
        using var developmentOwner = _hosts.OwnerBrowser(_hosts.Development, applicant);
        using var developmentStranger = _hosts.Development.NewBrowser();

        var productionPage = await productionOwner.GetAsync($"/signup/pending/{applicant.Id}");
        var developmentPage = await developmentOwner.GetAsync($"/signup/pending/{applicant.Id}");
        var strangerPage = await developmentStranger.GetAsync($"/signup/pending/{applicant.Id}");

        AssertHtml(productionPage);
        Assert.Contains($"/checkout/review/{applicant.Id}", productionPage.Body);
        Assert.DoesNotContain(note, productionPage.Body);
        AssertHtml(developmentPage);
        Assert.Contains(note, developmentPage.Body);
        AssertHtml(strangerPage);
        Assert.DoesNotContain(note, strangerPage.Body);
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.AwaitingPayment)]
    [InlineData(PendingRegistrationStatus.PaymentFailed)]
    public async Task ReviewPage_ShowsTheApplicationAndCancelOnlyToTheApplicant(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var page = await owner.GetAsync($"/checkout/review/{applicant.Id}");

        AssertHtml(page);
        var entity = applicant.Entity;
        foreach (var value in new[]
                 {
                     entity.OwnerFullName, entity.OwnerEmail, entity.OwnerPhone!, entity.BusinessName,
                     entity.BusinessPhone, entity.StreetAddress!, entity.BuildingNumber!, entity.Floor!,
                     entity.DoorNumber!, entity.AddressNote!, entity.PostalCode!
                 })
        {
            Assert.Contains(Html(value), page.Body);
        }

        Assert.Contains($"action=\"/checkout/cancel/{applicant.Id}\"", page.Body);
        Assert.DoesNotContain("/checkout/simulate-", page.Body);
        AssertNotStoredByCaches(page);
        AssertNoProofIssued(page);
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public async Task ReviewPage_RedirectsEveryBrowserWithoutValidProofToThePublicStatus(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        var someoneElse = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        var proof = _hosts.ProofFor(_hosts.Production, applicant.Id);
        var browsers = new[]
        {
            _hosts.Production.NewBrowser(),
            _hosts.OwnerBrowser(_hosts.Production, someoneElse),
            _hosts.BrowserWithProof(_hosts.Production, Tamper(proof)),
            _hosts.BrowserWithProof(_hosts.Production, _hosts.ProofFor(_hosts.Production, applicant.Id, DateTimeOffset.UtcNow.AddMinutes(-1)))
        };

        foreach (var browser in browsers)
        {
            using (browser)
            {
                var response = await browser.GetAsync($"/checkout/review/{applicant.Id}");
                AssertRedirectToPublicStatus(response, applicant.Id);
                AssertNoPrivateData(response, applicant);
            }
        }
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    public async Task SuccessPage_ShowsDetailsToTheApplicant_AndRedirectsAnyoneElse(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        using var stranger = _hosts.Production.NewBrowser();
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var strangerResponse = await stranger.GetAsync($"/checkout/success/{applicant.Id}");
        var ownerPage = await owner.GetAsync($"/checkout/success/{applicant.Id}");

        AssertRedirectToPublicStatus(strangerResponse, applicant.Id);
        AssertNoPrivateData(strangerResponse, applicant);

        AssertHtml(ownerPage);
        Assert.Contains(Html(applicant.Entity.OwnerFullName), ownerPage.Body);
        Assert.Contains(Html(applicant.Entity.OwnerEmail), ownerPage.Body);
        Assert.Contains(Html(applicant.Entity.BusinessName), ownerPage.Body);
        AssertNotStoredByCaches(ownerPage);
    }

    [Fact]
    public async Task FailedPage_IsGenericForEveryBrowser_AndOffersTheApplicantATryAgainLink()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.PaymentFailed);
        using var stranger = _hosts.Production.NewBrowser();
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var strangerPage = await stranger.GetAsync($"/checkout/failed/{applicant.Id}");
        var ownerPage = await owner.GetAsync($"/checkout/failed/{applicant.Id}");

        AssertHtml(strangerPage);
        AssertNoPrivateData(strangerPage, applicant);
        AssertHtml(ownerPage);
        AssertNoPrivateData(ownerPage, applicant);
        Assert.Contains($"href=\"/checkout/review/{applicant.Id}\"", ownerPage.Body);
    }

    [Fact]
    public async Task CancelledPage_IsGenericForEveryBrowser()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.Cancelled);
        using var stranger = _hosts.Production.NewBrowser();
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var strangerPage = await stranger.GetAsync($"/checkout/cancelled/{applicant.Id}");
        var ownerPage = await owner.GetAsync($"/checkout/cancelled/{applicant.Id}");

        AssertHtml(strangerPage);
        AssertNoPrivateData(strangerPage, applicant);
        AssertHtml(ownerPage);
        AssertNoPrivateData(ownerPage, applicant);
    }

    [Theory]
    [InlineData(PendingRegistrationStatus.AwaitingPayment)]
    [InlineData(PendingRegistrationStatus.PaymentSucceeded)]
    [InlineData(PendingRegistrationStatus.Provisioned)]
    public async Task PendingTenantHost_RedirectsWithOnlyTheId_AndTheStatusPageThereIsGeneric(PendingRegistrationStatus status)
    {
        var applicant = await _hosts.SeedAsync(status);
        var tenantHost = applicant.Entity.PrimaryDomain;
        using var stranger = _hosts.Production.NewBrowser();
        // The applicant's proof is host-only on the central host, so a browser never sends it here.
        using var owner = _hosts.OwnerBrowser(_hosts.Production, applicant);

        var redirect = await stranger.GetAsync("/", tenantHost);
        var strangerPage = await stranger.GetAsync(redirect.Location!, tenantHost);
        var ownerOnTenantHost = await owner.GetAsync($"/signup/pending/{applicant.Id}", tenantHost);

        Assert.Equal(HttpStatusCode.Redirect, redirect.Status);
        Assert.Equal($"/signup/pending/{applicant.Id}", redirect.Location);
        AssertNoPrivateData(redirect, applicant);
        AssertHtml(strangerPage);
        AssertNoPrivateData(strangerPage, applicant);
        AssertHtml(ownerOnTenantHost);
        AssertNoPrivateData(ownerOnTenantHost, applicant);
    }

    [Fact]
    public async Task CentralHostLookup_RedirectsWithOnlyTheId_ToTheGenericStatusPage()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var stranger = _hosts.Production.NewBrowser();

        var redirect = await stranger.GetAsync($"/tenant-not-found?host={applicant.Entity.PrimaryDomain}");
        var page = await stranger.GetAsync(redirect.Location!);

        Assert.Equal(HttpStatusCode.Redirect, redirect.Status);
        Assert.Equal($"/signup/pending/{applicant.Id}", redirect.Location);
        AssertNoPrivateData(redirect, applicant);
        AssertHtml(page);
        AssertNoPrivateData(page, applicant);
    }

    [Fact]
    public async Task SignupThenCheckout_TheSubmittingBrowserCanReviewAndCancel_AnotherBrowserCannot()
    {
        using var applicantBrowser = _hosts.Production.NewBrowser();
        var token = await applicantBrowser.GetAntiforgeryTokenAsync("/signup");
        var form = SentinelSignupForm(token);

        var submitted = await applicantBrowser.PostFormAsync("/signup", form);

        Assert.Equal(HttpStatusCode.Redirect, submitted.Status);
        Assert.StartsWith("/signup/pending/", submitted.Location);
        var registrationId = Guid.Parse(submitted.Location!["/signup/pending/".Length..]);
        var proofCookie = Assert.Single(submitted.SetCookies, c => c.Name.Value == SignupRegistrationOwnership.CookieName);
        Assert.True(proofCookie.Secure);
        Assert.True(proofCookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, proofCookie.SameSite);
        Assert.Equal("/", proofCookie.Path.Value);
        Assert.True(string.IsNullOrEmpty(proofCookie.Domain.Value));
        Assert.InRange(proofCookie.Expires!.Value, DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
        Assert.DoesNotContain(proofCookie.Value.Value!, submitted.Location);

        var ownerName = form.Single(f => f.Key == "OwnerFullName").Value;
        var ownerEmail = form.Single(f => f.Key == "OwnerEmail").Value;
        var pending = await applicantBrowser.GetAsync(submitted.Location!);
        Assert.Contains(Html(ownerName), pending.Body);
        Assert.Contains($"/checkout/review/{registrationId}", pending.Body);

        // Another browser with the ID and a valid antiforgery token of its own.
        using var stranger = _hosts.Production.NewBrowser();
        var strangerToken = await stranger.GetAntiforgeryTokenAsync("/signup");
        var strangerPending = await stranger.GetAsync(submitted.Location!);
        var strangerCancel = await stranger.PostFormAsync(
            $"/checkout/cancel/{registrationId}", [new("__RequestVerificationToken", strangerToken)]);
        var strangerSimulate = await stranger.PostFormAsync(
            $"/checkout/simulate-success/{registrationId}", [new("__RequestVerificationToken", strangerToken)]);
        Assert.DoesNotContain(Html(ownerName), strangerPending.Body);
        Assert.DoesNotContain(ownerName, strangerPending.Body);
        Assert.DoesNotContain(Html(ownerEmail), strangerPending.Body);
        Assert.DoesNotContain(ownerEmail, strangerPending.Body);
        Assert.Equal(HttpStatusCode.NotFound, strangerCancel.Status);
        Assert.Equal(HttpStatusCode.NotFound, strangerSimulate.Status);
        var afterStranger = await _hosts.ReloadAsync(registrationId);
        Assert.Equal(PendingRegistrationStatus.AwaitingPayment, afterStranger.Status);
        Assert.Null(afterStranger.PaymentSucceededAtUtc);

        var review = await applicantBrowser.GetAsync($"/checkout/review/{registrationId}");
        Assert.Equal(HttpStatusCode.OK, review.Status);
        Assert.Contains(Html(ownerEmail), review.Body);
        var cancel = await applicantBrowser.PostFormAsync(
            $"/checkout/cancel/{registrationId}",
            [new("__RequestVerificationToken", TestBrowser.AntiforgeryTokenIn(review.Body))]);

        Assert.Equal(HttpStatusCode.Redirect, cancel.Status);
        Assert.Equal($"/checkout/cancelled/{registrationId}", cancel.Location);
        Assert.Equal(PendingRegistrationStatus.Cancelled, (await _hosts.ReloadAsync(registrationId)).Status);
        var cancelledPage = await applicantBrowser.GetAsync(cancel.Location!);
        Assert.Equal(HttpStatusCode.OK, cancelledPage.Status);
    }

    [Fact]
    public async Task PaymentSimulation_InDevelopment_WorksForTheApplicantOnly()
    {
        var applicant = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        var victim = await _hosts.SeedAsync(PendingRegistrationStatus.AwaitingPayment);
        using var owner = _hosts.OwnerBrowser(_hosts.Development, applicant);
        using var stranger = _hosts.Development.NewBrowser();

        var strangerToken = await stranger.GetAntiforgeryTokenAsync("/signup");
        var strangerAttempt = await stranger.PostFormAsync(
            $"/checkout/simulate-success/{victim.Id}", [new("__RequestVerificationToken", strangerToken)]);
        var review = await owner.GetAsync($"/checkout/review/{applicant.Id}");
        var simulated = await owner.PostFormAsync(
            $"/checkout/simulate-success/{applicant.Id}",
            [new("__RequestVerificationToken", TestBrowser.AntiforgeryTokenIn(review.Body))]);
        var success = await owner.GetAsync(simulated.Location!);

        Assert.Equal(HttpStatusCode.NotFound, strangerAttempt.Status);
        Assert.Equal(PendingRegistrationStatus.AwaitingPayment, (await _hosts.ReloadAsync(victim.Id)).Status);
        Assert.Contains($"action=\"/checkout/simulate-success/{applicant.Id}\"", review.Body);
        Assert.Equal($"/checkout/success/{applicant.Id}", simulated.Location);
        Assert.Equal(PendingRegistrationStatus.PaymentSucceeded, (await _hosts.ReloadAsync(applicant.Id)).Status);
        Assert.Contains(Html(applicant.Entity.OwnerFullName), success.Body);
    }

    private static void AssertHtml(TestResponse response)
    {
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("text/html", response.ContentType);
    }

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    private static void AssertNoPrivateData(TestResponse response, SentinelApplicant applicant)
    {
        foreach (var value in applicant.PrivateValues)
        {
            Assert.DoesNotContain(value, response.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Html(value), response.Body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(applicant.PrivateValues, value =>
            (response.Location ?? string.Empty).Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRedirectToPublicStatus(TestResponse response, Guid registrationId)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.Status);
        Assert.Equal($"/signup/pending/{registrationId}", response.Location);
        Assert.True(string.IsNullOrEmpty(response.Body));
    }

    private static void AssertNoProofIssued(TestResponse response) =>
        Assert.DoesNotContain(response.SetCookies, c => c.Name.Value == SignupRegistrationOwnership.CookieName);

    // Responses that carry the applicant's details must not be kept by shared or browser caches,
    // because the same URL serves only the public status to anyone else.
    private static void AssertNotStoredByCaches(TestResponse response)
    {
        Assert.NotNull(response.CacheControl);
        Assert.Contains("no-store", response.CacheControl);
    }

    private static string Tamper(string proof) => proof[..^4] + (proof[^4] == 'A' ? "BBBB" : "AAAA");

    private static List<KeyValuePair<string, string>> SentinelSignupForm(string antiforgeryToken)
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
            new("Password", "Sentinel-pass-1"),
            new("ConfirmPassword", "Sentinel-pass-1")
        ];
    }

    /// <summary>A Production and a Development host over one isolated CentralDb and key ring.</summary>
    public sealed class Hosts : IAsyncLifetime
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-signup-keys-").FullName;

        internal SignupWebHost Production { get; private set; } = null!;

        internal SignupWebHost Development { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _connection.Open();
            await SignupWebHost.CreateCentralDbAsync(_connection);
            Production = await SignupWebHost.StartAsync(_connection, _keyRing, Environments.Production);
            Development = await SignupWebHost.StartAsync(_connection, _keyRing, Environments.Development);
        }

        public async ValueTask DisposeAsync()
        {
            await Production.DisposeAsync();
            await Development.DisposeAsync();
            _connection.Dispose();
            try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
        }

        internal async Task<SentinelApplicant> SeedAsync(PendingRegistrationStatus status)
        {
            var applicant = new SentinelApplicant(status);
            await using var db = NewDb();
            db.PendingRegistrations.Add(applicant.Entity);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return applicant;
        }

        internal async Task<PendingRegistration> ReloadAsync(Guid id)
        {
            await using var db = NewDb();
            return await db.PendingRegistrations.AsNoTracking()
                .SingleAsync(r => r.Id == id, TestContext.Current.CancellationToken);
        }

        /// <summary>Mints the proof exactly as a successful signup on that host would.</summary>
        internal string ProofFor(SignupWebHost host, Guid registrationId, DateTimeOffset? expiresAt = null) =>
            new SignupRegistrationOwnership(
                    host.Services.GetRequiredService<IDataProtectionProvider>(),
                    host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>())
                .CreateProof(registrationId, expiresAt ?? DateTimeOffset.UtcNow.AddDays(7));

        internal TestBrowser OwnerBrowser(SignupWebHost host, SentinelApplicant applicant) =>
            BrowserWithProof(host, ProofFor(host, applicant.Id));

        internal TestBrowser BrowserWithProof(SignupWebHost host, string proof)
        {
            var browser = host.NewBrowser();
            browser.SetCookie(SignupWebHost.CentralHost, SignupRegistrationOwnership.CookieName, proof);
            return browser;
        }

        private CentralDbContext NewDb() =>
            new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);
    }
}

/// <summary>A registration whose every private field holds a value unique to this test run.</summary>
internal sealed class SentinelApplicant
{
    public SentinelApplicant(PendingRegistrationStatus status)
    {
        var token = NewToken();
        var now = DateTime.UtcNow;
        Entity = new PendingRegistration
        {
            Id = Guid.NewGuid(),
            Status = status,
            Slug = $"s{token}",
            PrimaryDomain = $"s{token}.wasla.local",
            DatabaseName = $"Wasla_s{token}",
            BusinessName = $"Bizname{token}",
            BusinessPhone = NewPhone(),
            BusinessEmail = $"biz.{token}@sentinel.test",
            Country = "TR",
            City = "Istanbul",
            District = "Kadikoy",
            Neighborhood = $"Hoodq{token}",
            StreetAddress = $"Streetq{token}",
            BuildingNumber = $"Bldq{token}",
            Floor = $"Flrq{token}",
            DoorNumber = $"Doorq{token}",
            AddressNote = $"Noteq{token}",
            PostalCode = $"Pcq{token}",
            OwnerFullName = $"Ownerq{token}",
            OwnerEmail = $"owner.{token}@sentinel.test",
            OwnerPhone = NewPhone(),
            PasswordHash = $"Hashq{token}",
            PlanCode = WaslaPlanCodes.Starter,
            BillingPeriod = "Monthly",
            CreatedAtUtc = now,
            ExpiresAtUtc = status == PendingRegistrationStatus.Expired ? now.AddMinutes(-5) : now.AddDays(7),
            PaymentSucceededAtUtc = status is PendingRegistrationStatus.PaymentSucceeded or PendingRegistrationStatus.Provisioned ? now : null,
            PaymentFailedAtUtc = status == PendingRegistrationStatus.PaymentFailed ? now : null,
            ProvisionedAtUtc = status == PendingRegistrationStatus.Provisioned ? now : null,
            SimulatedPaymentReference = status is PendingRegistrationStatus.PaymentSucceeded or PendingRegistrationStatus.Provisioned
                ? $"SIM-Refq{token}"
                : null
        };
    }

    public PendingRegistration Entity { get; }

    public Guid Id => Entity.Id;

    /// <summary>Every value an ID-only browser must never receive, including formatted phones.</summary>
    public IEnumerable<string> PrivateValues
    {
        get
        {
            yield return Entity.OwnerFullName;
            yield return Entity.OwnerEmail;
            yield return Entity.BusinessName;
            yield return Entity.BusinessEmail!;
            yield return Entity.Neighborhood!;
            yield return Entity.StreetAddress!;
            yield return Entity.BuildingNumber!;
            yield return Entity.Floor!;
            yield return Entity.DoorNumber!;
            yield return Entity.AddressNote!;
            yield return Entity.PostalCode!;
            yield return Entity.PasswordHash;
            yield return Entity.SimulatedPaymentReference ?? Entity.PasswordHash;
            foreach (var phone in new[] { Entity.OwnerPhone!, Entity.BusinessPhone })
            {
                yield return phone;
                yield return phone[3..];
                yield return TurkishPhoneDisplayFormatter.FormatWithCountryCode(phone);
                yield return TurkishPhoneDisplayFormatter.FormatLocalMobile(phone);
            }
        }
    }

    public static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A Turkish mobile-shaped number that is unique per call.</summary>
    public static string NewPhone() => "5" + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString();
}
