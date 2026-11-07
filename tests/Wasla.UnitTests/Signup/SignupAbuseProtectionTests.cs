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
/// Abuse limits on anonymous signup (WAS-90, WAS-192): the per-network limits on the registration draft submit and draft
/// creation and on the checkout POSTs that change a registration, and the cap on active registrations per owner email. Every test serves the real signup and
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

    private async Task<SignupWebHost> StartAsync(
        int signupLimit = 1000,
        int checkoutLimit = 1000,
        string environment = "Production",
        int draftCreationLimit = 1000)
    {
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, environment, configuration: new Dictionary<string, string?>
        {
            ["AuthenticationRateLimiting:SignupSubmission:PermitLimit"] = signupLimit.ToString(CultureInfo.InvariantCulture),
            ["AuthenticationRateLimiting:SignupCheckout:PermitLimit"] = checkoutLimit.ToString(CultureInfo.InvariantCulture),
            ["AuthenticationRateLimiting:SignupDraftCreation:PermitLimit"] = draftCreationLimit.ToString(CultureInfo.InvariantCulture)
        });
        return _host;
    }

    // --- Per-network limit ------------------------------------------------------------------------

    [Fact]
    public async Task DraftSubmitOverTheNetworkLimit_Gets429WithRetryAfter_AndCreatesNoRegistration()
    {
        var host = await StartAsync(signupLimit: 2);
        for (var i = 0; i < 2; i++)
        {
            using var accepted = new SignupDraftClient(host);
            await accepted.RegisterAsync();
        }

        Assert.Equal(2, await RegistrationCountAsync());
        using var client = new SignupDraftClient(host);

        var limited = await client.CompleteAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("Signup.TooManyAttempts", limited.Json.GetProperty("code").GetString());
        Assert.InRange(limited.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        Assert.Equal(2, await RegistrationCountAsync());
        Assert.DoesNotContain(limited.SetCookies, cookie => cookie.Name.Value == SignupRegistrationOwnership.CookieName);
        Assert.DoesNotContain(SignupDraftClient.Password, limited.Body, StringComparison.Ordinal);
        // The draft is kept: the browser can submit it once the window has passed.
        Assert.True((await client.GetDraftAsync()).Json.GetProperty("canReview").GetBoolean());
    }

    [Fact]
    public async Task Antiforgery_RunsBeforeTheLimit_AndARejectedTokenUsesNoPermit()
    {
        var host = await StartAsync(signupLimit: 1);
        using var client = new SignupDraftClient(host);
        await client.SaveSectionsAsync();
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(client.LatestCode()));

        var withoutToken = await client.PostAsync("/signup/draft/submit", antiforgery: false);
        var accepted = await client.SubmitAsync();

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.Status);
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(1, await RegistrationCountAsync());
    }

    [Fact]
    public async Task DraftCreationOverItsNetworkLimit_Gets429_AndAnExistingDraftCanStillBeSaved()
    {
        var host = await StartAsync(draftCreationLimit: 1);
        using var first = new SignupDraftClient(host);
        await SignupDraftClient.AssertOkAsync(first.SavePlanAsync());
        using var second = new SignupDraftClient(host);

        var refused = await second.SavePlanAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Equal("Signup.TooManyAttempts", refused.Json.GetProperty("code").GetString());
        Assert.NotNull(refused.RetryAfter);
        Assert.Null(second.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName));
        await SignupDraftClient.AssertOkAsync(first.SaveRestaurantAsync());
    }

    // --- Per-email cap --------------------------------------------------------------------------------

    [Fact]
    public async Task OneOwnerEmail_GetsAtMostThreeActiveRegistrations_WithTheSameAnswerAsTheNetworkLimit()
    {
        var host = await StartAsync();
        var email = $"owner.{SentinelApplicant.NewToken()}@sentinel.test";

        for (var i = 0; i < 3; i++)
        {
            using var client = new SignupDraftClient(host);
            await client.RegisterAsync(email: email);
        }

        // Upper case does not make it another email.
        using var refusedClient = new SignupDraftClient(host);
        var refused = await refusedClient.CompleteAsync(email: email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Equal("Signup.TooManyAttempts", refused.Json.GetProperty("code").GetString());
        Assert.Equal(3, await RegistrationCountAsync(email));

        // The cap counts active registrations only: once one is cancelled, the same draft may be submitted.
        await using (var db = NewDb())
        {
            var one = await db.PendingRegistrations.FirstAsync(r => r.OwnerEmail == email, TestContext.Current.CancellationToken);
            one.Status = PendingRegistrationStatus.Cancelled;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, (await refusedClient.SubmitAsync()).Status);
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
        var strangerToken = host.IssueAntiforgeryToken(stranger);
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
        // Outside Development the link leads back to the registration summary, not to a payment step.
        Assert.Contains(Localized(host, "tr-TR", "Checkout.ReturnToRegistrationSummary"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Localized(host, "tr-TR", "Checkout.TryAgain"), html, StringComparison.Ordinal);
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
        Assert.Contains(Localized(host, "tr-TR", "Checkout.TryAgain"), WebUtility.HtmlDecode(success.Body), StringComparison.Ordinal);
        await using var check = NewDb();
        var stored = await check.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Id == applicant.Id, TestContext.Current.CancellationToken);
        Assert.Equal(PendingRegistrationStatus.PaymentFailed, stored.Status);
        Assert.Null(stored.PaymentSucceededAtUtc);
    }

    // --- Helpers --------------------------------------------------------------------------------------

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
