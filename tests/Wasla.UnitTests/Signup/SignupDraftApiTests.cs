using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Sms;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The registration draft and mandatory owner phone verification over HTTP (WAS-192): the real draft API, signup page,
/// services and Data Protection in the Production environment (<see cref="SignupWebHost"/>) over an in-memory SQLite
/// CentralDb, with the fake SMS sender and a test clock. Assertions are on status codes, stable error codes and stored
/// state, never on localized text. All data is fake; no SMS is sent.
/// </summary>
public sealed class SignupDraftApiTests : IClassFixture<SignupDraftApiTests.Host>
{
    private readonly Host _host;

    public SignupDraftApiTests(Host host)
    {
        _host = host;
        _host.Web.FakeSms!.FailDeliveriesWith = null;
    }

    public sealed class Host : IAsyncLifetime
    {
        private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-signup-draft-keys-").FullName;

        public SqliteConnection Connection { get; } = new("Data Source=:memory:");

        public SignupTestClock Clock { get; } = new(DateTime.UtcNow);

        internal SignupWebHost Web { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            Connection.Open();
            await SignupWebHost.CreateCentralDbAsync(Connection);
            Web = await SignupWebHost.StartAsync(Connection, _keyRing, configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Web.DisposeAsync();
            Connection.Dispose();
            try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
        }

        internal CentralDbContext Db() => new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(Connection).Options);
    }

    private SignupDraftClient Client() => new(_host.Web);

    // --- Draft sections ---------------------------------------------------------------------------------

    [Fact]
    public async Task Sections_SaveAndEdit_RoundTripWithoutEverReturningThePasswordOrItsHash()
    {
        using var client = Client();
        var empty = await SignupDraftClient.AssertOkAsync(client.GetDraftAsync());
        Assert.False(empty.Json.GetProperty("exists").GetBoolean());

        var plan = await SignupDraftClient.AssertOkAsync(client.SavePlanAsync("Pro", "Yearly"));
        Assert.Contains(plan.SetCookies, c => c.Name.Value == SignupDraftCookie.CookieName && c.HttpOnly && c.Path.Value == "/signup");
        var restaurant = SignupDraftClient.Restaurant();
        await SignupDraftClient.AssertOkAsync(client.SaveRestaurantAsync(restaurant));
        var owner = client.Owner();
        await SignupDraftClient.AssertOkAsync(client.SaveOwnerAsync(owner));

        var state = (await SignupDraftClient.AssertOkAsync(client.GetDraftAsync())).Json;
        Assert.True(state.GetProperty("exists").GetBoolean());
        Assert.Equal("Pro", state.GetProperty("plan").GetProperty("planCode").GetString());
        Assert.Equal("Yearly", state.GetProperty("plan").GetProperty("billingPeriod").GetString());
        Assert.Equal(restaurant["slug"], state.GetProperty("restaurant").GetProperty("slug").GetString());
        Assert.Equal(restaurant["businessName"], state.GetProperty("restaurant").GetProperty("businessName").GetString());
        Assert.Equal(owner["email"], state.GetProperty("owner").GetProperty("email").GetString());
        Assert.True(state.GetProperty("owner").GetProperty("passwordSet").GetBoolean());
        Assert.False(state.GetProperty("owner").TryGetProperty("password", out _));
        Assert.False(state.GetProperty("canReview").GetBoolean());

        // An owner edit without a password keeps the stored hash; the restaurant edit changes only its section.
        var hashBefore = await StoredHashAsync(client);
        owner["fullName"] = "Changed Owner";
        owner["password"] = null;
        owner["confirmPassword"] = null;
        await SignupDraftClient.AssertOkAsync(client.SaveOwnerAsync(owner));
        restaurant["businessName"] = "Changed Restaurant";
        var edited = (await SignupDraftClient.AssertOkAsync(client.SaveRestaurantAsync(restaurant))).Json;
        Assert.Equal("Changed Restaurant", edited.GetProperty("restaurant").GetProperty("businessName").GetString());
        Assert.Equal("Changed Owner", edited.GetProperty("owner").GetProperty("fullName").GetString());
        Assert.Equal(hashBefore, await StoredHashAsync(client));
        Assert.True(BCrypt.Net.BCrypt.Verify(SignupDraftClient.Password, hashBefore));

        foreach (var body in new[] { plan.Body, edited.GetRawText(), state.GetRawText() })
        {
            Assert.DoesNotContain(SignupDraftClient.Password, body, StringComparison.Ordinal);
            Assert.DoesNotContain(hashBefore, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OwnerSection_RequiresAPasswordFirst_AndAValidTurkishMobile()
    {
        using var client = Client();

        var noPassword = await client.SaveOwnerAsync(client.Owner(password: null));
        var noMobile = await client.SaveOwnerAsync(client.Owner(mobile: ""));
        var landline = await client.SaveOwnerAsync(client.Owner(mobile: "0212 555 12 34"));
        var foreign = await client.SaveOwnerAsync(client.Owner(mobile: "+44 7700 900123"));

        AssertFieldError(noPassword, "owner", "password", "Validation.PasswordRequired");
        AssertFieldError(noMobile, "owner", "mobilePhone", "Validation.MobilePhoneRequired");
        AssertFieldError(landline, "owner", "mobilePhone", "Validation.MobilePhoneInvalid");
        AssertFieldError(foreign, "owner", "mobilePhone", "Validation.MobilePhoneInvalid");
        Assert.Equal(0, await DraftCountForAsync(client));

        // Typed with spaces and a leading 0: stored in E.164, answered as the national number.
        var saved = (await SignupDraftClient.AssertOkAsync(client.SaveOwnerAsync(client.Owner(mobile: client.MobileTyped)))).Json;
        Assert.Equal(client.MobileE164[3..], saved.GetProperty("owner").GetProperty("mobilePhone").GetString());
        Assert.Equal("+90 5** *** ** " + client.MobileE164[^2..], saved.GetProperty("phone").GetProperty("maskedPhone").GetString());
        await using var db = _host.Db();
        Assert.True(await db.RegistrationDrafts.AnyAsync(d => d.OwnerMobilePhone == client.MobileE164, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidSections_AreRefusedWithFieldCodes_AndCreateNoDraft()
    {
        using var client = Client();

        var plan = await client.SavePlanAsync("Enterprise");
        var restaurant = SignupDraftClient.Restaurant(slug: "Bad Slug!");
        restaurant["businessName"] = "";
        var invalidRestaurant = await client.SaveRestaurantAsync(restaurant);
        var reserved = await client.SaveRestaurantAsync(SignupDraftClient.Restaurant(slug: "admin"));
        var noBody = await client.PutAsync("/signup/draft/plan", null);

        AssertFieldError(plan, "plan", "planCode", "Signup.EnterpriseContactOnly");
        AssertFieldError(invalidRestaurant, "restaurant", "businessName", "Validation.BusinessNameRequired");
        AssertFieldError(invalidRestaurant, "restaurant", "slug", "Validation.SlugInvalid");
        AssertFieldError(reserved, "restaurant", "slug", "Signup.SlugUnavailable");
        AssertError(noBody, HttpStatusCode.BadRequest, "Signup.Draft.InvalidRequest");
        Assert.DoesNotContain(plan.SetCookies, c => c.Name.Value == SignupDraftCookie.CookieName);
        Assert.Null(client.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName));
    }

    [Fact]
    public async Task Writes_NeedTheAntiforgeryToken()
    {
        using var client = Client();

        var refused = await client.PutAsync("/signup/draft/plan", new Dictionary<string, object?> { ["planCode"] = "Starter", ["billingPeriod"] = "Monthly" }, antiforgery: false);

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Null(client.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName));
    }

    [Fact]
    public async Task ASignupPage_IsNeverCached()
    {
        using var client = Client();
        await SignupDraftClient.AssertOkAsync(client.SaveOwnerAsync());

        var page = await client.Browser.GetAsync("/signup");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("no-store", page.CacheControl, StringComparison.OrdinalIgnoreCase);
    }

    // --- Happy path and submit boundary ---------------------------------------------------------------

    [Fact]
    public async Task SendVerifySubmit_CreatesExactlyOneAwaitingPaymentRegistration_WithTheHashAndTheVerifiedPhone()
    {
        using var client = Client();
        var slug = SignupDraftClient.NewSlug();
        await client.SaveSectionsAsync(slug);

        var sent = await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        var code = client.LatestCode();
        Assert.Equal("codeSent", sent.Json.GetProperty("state").GetString());
        Assert.Equal(6, sent.Json.GetProperty("codeLength").GetInt32());
        Assert.InRange(sent.Json.GetProperty("codeExpiresInSeconds").GetInt32(), 290, 300);
        Assert.Equal(5, sent.Json.GetProperty("attemptsRemaining").GetInt32());
        Assert.DoesNotContain(code, sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(code, (await client.GetDraftAsync()).Body, StringComparison.Ordinal);

        var verified = await SignupDraftClient.AssertOkAsync(client.VerifyAsync(code));
        Assert.Equal("verified", verified.Json.GetProperty("state").GetString());
        Assert.True((await client.GetDraftAsync()).Json.GetProperty("canReview").GetBoolean());

        var submitted = await SignupDraftClient.AssertOkAsync(client.SubmitAsync());
        var registrationId = submitted.Json.GetProperty("registrationId").GetGuid();
        Assert.Equal($"/signup/pending/{registrationId}", submitted.Json.GetProperty("redirectUrl").GetString());
        var proof = Assert.Single(submitted.SetCookies, c => c.Name.Value == SignupRegistrationOwnership.CookieName);
        Assert.True(proof.HttpOnly);
        Assert.True(proof.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, proof.SameSite);
        Assert.Null(client.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName));

        await using var db = _host.Db();
        var registration = await db.PendingRegistrations.AsNoTracking().SingleAsync(r => r.Slug == slug, TestContext.Current.CancellationToken);
        Assert.Equal(registrationId, registration.Id);
        Assert.Equal(PendingRegistrationStatus.AwaitingPayment, registration.Status);
        Assert.Equal(client.MobileE164, registration.OwnerPhone);
        Assert.NotNull(registration.OwnerPhoneVerifiedAtUtc);
        Assert.True(BCrypt.Net.BCrypt.Verify(SignupDraftClient.Password, registration.PasswordHash));
        var draft = await db.RegistrationDrafts.AsNoTracking().SingleAsync(d => d.PendingRegistrationId == registrationId, TestContext.Current.CancellationToken);
        Assert.NotNull(draft.SubmittedAtUtc);
        Assert.Null(draft.PasswordHash);
        Assert.Null(draft.OwnerMobilePhone);
        Assert.Null(draft.OwnerEmail);
        Assert.Null(draft.BusinessName);
        Assert.False(await db.RegistrationPhoneVerifications.AnyAsync(r => r.DraftId == draft.Id, TestContext.Current.CancellationToken));

        // The browser that submitted sees its private details on the status page; another browser only the status.
        var mine = await client.Browser.GetAsync(submitted.Json.GetProperty("redirectUrl").GetString()!);
        using var stranger = _host.Web.NewBrowser();
        var theirs = await stranger.GetAsync($"/signup/pending/{registrationId}");
        Assert.Contains(registration.OwnerEmail, WebUtility.HtmlDecode(mine.Body), StringComparison.Ordinal);
        Assert.DoesNotContain(registration.OwnerEmail, WebUtility.HtmlDecode(theirs.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedSubmit_OfOneDraft_ReturnsTheSameRegistration()
    {
        using var client = Client();
        await client.SaveSectionsAsync();
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(client.LatestCode()));
        // A double click: the second request carries the draft cookie the first answer clears.
        var draftCookie = client.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName)!;

        var first = await SignupDraftClient.AssertOkAsync(client.SubmitAsync());
        client.Browser.SetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName, draftCookie);
        var second = await SignupDraftClient.AssertOkAsync(client.SubmitAsync());

        var id = first.Json.GetProperty("registrationId").GetGuid();
        Assert.Equal(id, second.Json.GetProperty("registrationId").GetGuid());
        await using var db = _host.Db();
        Assert.Equal(1, await db.RegistrationDrafts.CountAsync(d => d.PendingRegistrationId == id, TestContext.Current.CancellationToken));
        Assert.True(await db.PendingRegistrations.AnyAsync(r => r.Id == id, TestContext.Current.CancellationToken));

        // A submitted draft can no longer be changed.
        client.Browser.SetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName, draftCookie);
        var edit = await client.SavePlanAsync();
        AssertError(edit, HttpStatusCode.Conflict, "Signup.Draft.Submitted");
        Assert.Equal($"/signup/pending/{id}", edit.Json.GetProperty("registrationUrl").GetString());
    }

    [Fact]
    public async Task Submit_UsesOnlyTheStoredDraft_AndIgnoresEveryBodyField()
    {
        using var client = Client();
        var slug = SignupDraftClient.NewSlug();
        var email = $"owner.{SentinelApplicant.NewToken()}@sentinel.test";
        await client.SaveSectionsAsync(slug, email);
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(client.LatestCode()));

        var submitted = await SignupDraftClient.AssertOkAsync(client.SubmitAsync(new Dictionary<string, object?>
        {
            ["slug"] = "injected-slug",
            ["ownerEmail"] = "attacker@sentinel.test",
            ["ownerPhone"] = "+905000000000",
            ["password"] = "Injected-pass-1",
            ["planCode"] = "ProPlus",
            ["phoneVerified"] = true
        }));

        await using var db = _host.Db();
        var registration = await db.PendingRegistrations.AsNoTracking()
            .SingleAsync(r => r.Id == submitted.Json.GetProperty("registrationId").GetGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(slug, registration.Slug);
        Assert.Equal(email, registration.OwnerEmail);
        Assert.Equal(client.MobileE164, registration.OwnerPhone);
        Assert.Equal("Starter", registration.PlanCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(SignupDraftClient.Password, registration.PasswordHash));
        Assert.False(await db.PendingRegistrations.AnyAsync(r => r.Slug == "injected-slug", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Submit_IsRefused_WithoutVerification_WithIncompleteSections_OrAfterTheNumberChanged()
    {
        using var incomplete = Client();
        await SignupDraftClient.AssertOkAsync(incomplete.SavePlanAsync());
        var missing = await incomplete.SubmitAsync();
        AssertError(missing, HttpStatusCode.Conflict, "Signup.Draft.Incomplete");
        Assert.Equal(new[] { "restaurant", "owner" }, missing.Json.GetProperty("missingSections").EnumerateArray().Select(e => e.GetString()));

        using var unverified = Client();
        await unverified.SaveSectionsAsync();
        AssertError(await unverified.SubmitAsync(), HttpStatusCode.Conflict, "Signup.Phone.NotVerified");
        // A code that was only sent is not a proof either.
        await SignupDraftClient.AssertOkAsync(unverified.SendCodeAsync());
        AssertError(await unverified.SubmitAsync(), HttpStatusCode.Conflict, "Signup.Phone.NotVerified");

        using var changed = Client();
        await changed.SaveSectionsAsync();
        await SignupDraftClient.AssertOkAsync(changed.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(changed.VerifyAsync(changed.LatestCode()));
        var other = SignupDraftClient.NewMobileE164();
        var status = await SignupDraftClient.AssertOkAsync(changed.ChangePhoneAsync(other));
        Assert.Equal("notSent", status.Json.GetProperty("state").GetString());
        AssertError(await changed.SubmitAsync(), HttpStatusCode.Conflict, "Signup.Phone.NotVerified");

        await using var db = _host.Db();
        foreach (var client in new[] { incomplete, unverified, changed })
            Assert.False(await db.PendingRegistrations.AnyAsync(r => r.OwnerPhone == client.MobileE164 || r.OwnerPhone == other, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForgedForeignOrUnknownDraftCookies_NameNoDraft()
    {
        using var victim = Client();
        await victim.SaveSectionsAsync();
        await SignupDraftClient.AssertOkAsync(victim.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(victim.VerifyAsync(victim.LatestCode()));
        var dataProtection = _host.Web.Services.GetRequiredService<IDataProtectionProvider>();
        var environment = _host.Web.Services.GetRequiredService<IWebHostEnvironment>();
        var ownership = new SignupRegistrationOwnership(dataProtection, environment);
        var draftCookie = new SignupDraftCookie(dataProtection, environment);
        var cookies = new[]
        {
            "not-a-draft",
            // Another purpose's payload for an existing id is not a draft reference.
            ownership.CreateProof(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
            // A well-formed reference to a draft that does not exist.
            draftCookie.CreateValue(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
            // An expired reference.
            draftCookie.CreateValue(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1))
        };

        foreach (var value in cookies)
        {
            using var attacker = Client();
            attacker.Browser.SetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName, value);

            AssertError(await attacker.SubmitAsync(), HttpStatusCode.Conflict, "Signup.Draft.Missing");
            attacker.Browser.SetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName, value);
            AssertError(await attacker.SendCodeAsync(), HttpStatusCode.Conflict, "Signup.Draft.Missing");
            attacker.Browser.SetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName, value);
            AssertError(await attacker.SavePlanAsync(), HttpStatusCode.Conflict, "Signup.Draft.Missing");
            Assert.Null(attacker.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName));
        }

        await using var db = _host.Db();
        Assert.False(await db.PendingRegistrations.AnyAsync(r => r.OwnerPhone == victim.MobileE164, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredDraft_IsMissing()
    {
        using var client = Client();
        await SignupDraftClient.AssertOkAsync(client.SavePlanAsync());

        _host.Clock.Advance(TimeSpan.FromHours(25));

        AssertError(await client.SaveOwnerAsync(), HttpStatusCode.Conflict, "Signup.Draft.Missing");
        Assert.Null(client.Browser.GetCookie(SignupWebHost.CentralHost, SignupDraftCookie.CookieName));
        Assert.False((await SignupDraftClient.AssertOkAsync(client.GetDraftAsync())).Json.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task TheOldFormPost_CannotCreateARegistration()
    {
        using var browser = _host.Web.NewBrowser();
        var token = _host.Web.IssueAntiforgeryToken(browser);
        var slug = SignupDraftClient.NewSlug();

        var posted = await browser.PostFormAsync("/signup",
        [
            new("__RequestVerificationToken", token),
            new("PlanCode", "Starter"),
            new("BillingPeriod", "Monthly"),
            new("BusinessName", "Form Restaurant"),
            new("SelectedBusinessTypeCodes", "burger"),
            new("BusinessPhoneType", "Mobile"),
            new("BusinessPhone", "5551112233"),
            new("Slug", slug),
            new("Country", "Germany"),
            new("City", "Berlin"),
            new("District", "Mitte"),
            new("StreetAddress", "Main Street 1"),
            new("OwnerFullName", "Form Owner"),
            new("OwnerEmail", "form.owner@sentinel.test"),
            new("Password", "Form-pass-1"),
            new("ConfirmPassword", "Form-pass-1")
        ]);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.Status);
        Assert.DoesNotContain(posted.SetCookies, c => c.Name.Value == SignupRegistrationOwnership.CookieName);
        await using var db = _host.Db();
        Assert.False(await db.PendingRegistrations.AnyAsync(r => r.Slug == slug, TestContext.Current.CancellationToken));
    }

    // --- Codes --------------------------------------------------------------------------------------------

    [Fact]
    public async Task WrongExpiredAndMalformedCodes_AreRefused()
    {
        using var client = Client();
        await client.SaveSectionsAsync();
        AssertError(await client.VerifyAsync("123456"), HttpStatusCode.Conflict, "Signup.Phone.NoActiveCode");
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        var code = client.LatestCode();

        var malformed = await client.VerifyAsync("12ab56");
        var wrong = await client.VerifyAsync(Wrong(code));

        AssertError(malformed, HttpStatusCode.BadRequest, "Signup.Phone.CodeFormatInvalid");
        AssertError(wrong, HttpStatusCode.BadRequest, "Signup.Phone.CodeIncorrect");
        Assert.Equal(4, wrong.Json.GetProperty("phone").GetProperty("attemptsRemaining").GetInt32());

        _host.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var expired = await client.VerifyAsync(code);
        AssertError(expired, HttpStatusCode.BadRequest, "Signup.Phone.CodeExpired");
        Assert.Equal("expired", expired.Json.GetProperty("phone").GetProperty("state").GetString());
        AssertError(await client.SubmitAsync(), HttpStatusCode.Conflict, "Signup.Phone.NotVerified");
    }

    [Fact]
    public async Task ReplayedSupersededAndCrossDraftCodes_AreRefused()
    {
        using var first = Client();
        using var second = Client();
        second.MobileE164 = first.MobileE164;
        await first.SaveSectionsAsync();
        await second.SaveSectionsAsync();

        await SignupDraftClient.AssertOkAsync(first.SendCodeAsync());
        var superseded = first.LatestCode();
        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        await SignupDraftClient.AssertOkAsync(first.SendCodeAsync());
        var current = first.LatestCode();
        Assert.NotEqual(superseded, current);

        AssertError(await first.VerifyAsync(superseded), HttpStatusCode.BadRequest, "Signup.Phone.CodeIncorrect");
        // The other draft for the same number has no code of its own; this draft's code does not verify it.
        AssertError(await second.VerifyAsync(current), HttpStatusCode.Conflict, "Signup.Phone.NoActiveCode");
        await SignupDraftClient.AssertOkAsync(second.SendCodeAsync());
        AssertError(await second.VerifyAsync(current), HttpStatusCode.BadRequest, "Signup.Phone.CodeIncorrect");

        await SignupDraftClient.AssertOkAsync(first.VerifyAsync(current));
        AssertError(await first.VerifyAsync(current), HttpStatusCode.Conflict, "Signup.Phone.AlreadyVerified");
    }

    [Fact]
    public async Task ChangingTheNumber_InvalidatesItsCodesAndProof_WhileRestaurantEditsKeepTheProof()
    {
        using var client = Client();
        await client.SaveSectionsAsync();
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        var oldNumber = client.MobileE164;
        var oldCode = client.LatestCode();

        // A code sent to the previous number never verifies the new one.
        client.MobileE164 = SignupDraftClient.NewMobileE164();
        var changed = (await SignupDraftClient.AssertOkAsync(client.SaveOwnerAsync(client.Owner(password: null)))).Json;
        Assert.Equal("notSent", changed.GetProperty("phone").GetProperty("state").GetString());
        AssertError(await client.VerifyAsync(oldCode), HttpStatusCode.Conflict, "Signup.Phone.NoActiveCode");

        // Back to the first number: its outstanding code was cleared by the change too.
        Assert.Equal(HttpStatusCode.OK, (await client.ChangePhoneAsync(oldNumber)).Status);
        AssertError(await client.VerifyAsync(oldCode), HttpStatusCode.Conflict, "Signup.Phone.NoActiveCode");

        client.MobileE164 = oldNumber;
        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(client.LatestCode()));
        var edited = (await SignupDraftClient.AssertOkAsync(client.SaveRestaurantAsync())).Json;
        Assert.Equal("verified", edited.GetProperty("phone").GetProperty("state").GetString());
        // Same number again: nothing to verify again.
        Assert.Equal("verified", (await SignupDraftClient.AssertOkAsync(client.ChangePhoneAsync(client.MobileTyped))).Json.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SubmitAsync()).Status);
    }

    [Fact]
    public async Task FiveWrongCodes_LockTheNumber_AndAResendDoesNotResetTheFailures()
    {
        using var client = Client();
        await client.SaveSectionsAsync();
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        var code = client.LatestCode();
        await client.VerifyAsync(Wrong(code));
        await client.VerifyAsync(Wrong(code));

        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        var resent = await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        Assert.Equal(3, resent.Json.GetProperty("attemptsRemaining").GetInt32());
        code = client.LatestCode();
        await client.VerifyAsync(Wrong(code));
        var last = await client.VerifyAsync(Wrong(code));
        Assert.Equal(1, last.Json.GetProperty("phone").GetProperty("attemptsRemaining").GetInt32());

        var locked = await client.VerifyAsync(Wrong(code));
        AssertError(locked, HttpStatusCode.TooManyRequests, "Signup.Phone.Locked");
        Assert.InRange(locked.RetryAfter!.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Equal("locked", locked.Json.GetProperty("phone").GetProperty("state").GetString());
        // Even the right code and a new send wait for the lock.
        AssertError(await client.VerifyAsync(code), HttpStatusCode.TooManyRequests, "Signup.Phone.Locked");
        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        AssertError(await client.SendCodeAsync(), HttpStatusCode.TooManyRequests, "Signup.Phone.Locked");

        _host.Clock.Advance(TimeSpan.FromMinutes(15));
        var fresh = await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        Assert.Equal(5, fresh.Json.GetProperty("attemptsRemaining").GetInt32());
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(client.LatestCode()));
    }

    [Fact]
    public async Task Resend_WaitsSixtySeconds()
    {
        using var client = Client();
        await client.SaveSectionsAsync();
        var sent = await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        Assert.InRange(sent.Json.GetProperty("resendAvailableInSeconds").GetInt32(), 59, 60);

        var early = await client.SendCodeAsync();

        AssertError(early, HttpStatusCode.TooManyRequests, "Signup.Phone.ResendNotAvailable");
        Assert.InRange(early.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Single(_host.Web.FakeSms!.MessagesTo(client.MobileE164));
        _host.Clock.Advance(TimeSpan.FromSeconds(60));
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        Assert.Equal(2, _host.Web.FakeSms!.MessagesTo(client.MobileE164).Count);
    }

    [Fact]
    public async Task ADeliveryFailure_LeavesNoUsableCode_AndKeepsTheResendWait()
    {
        using var client = Client();
        await client.SaveSectionsAsync();
        _host.Web.FakeSms!.FailDeliveriesWith = SmsDeliveryFailure.Rejected;

        var failed = await client.SendCodeAsync();

        AssertError(failed, HttpStatusCode.BadGateway, "Signup.Phone.DeliveryFailed");
        Assert.Equal("notSent", failed.Json.GetProperty("phone").GetProperty("state").GetString());
        Assert.Empty(_host.Web.FakeSms.MessagesTo(client.MobileE164));
        _host.Web.FakeSms.FailDeliveriesWith = null;
        AssertError(await client.SendCodeAsync(), HttpStatusCode.TooManyRequests, "Signup.Phone.ResendNotAvailable");
        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        await SignupDraftClient.AssertOkAsync(client.SendCodeAsync());
        await SignupDraftClient.AssertOkAsync(client.VerifyAsync(client.LatestCode()));
    }

    [Fact]
    public async Task PhoneOperations_NeedTheOwnerSectionFirst()
    {
        using var client = Client();
        await SignupDraftClient.AssertOkAsync(client.SavePlanAsync());

        AssertError(await client.SendCodeAsync(), HttpStatusCode.Conflict, "Signup.Phone.OwnerIncomplete");
        AssertError(await client.ChangePhoneAsync(client.MobileTyped), HttpStatusCode.Conflict, "Signup.Phone.OwnerIncomplete");
        Assert.Empty(_host.Web.FakeSms!.MessagesTo(client.MobileE164));
    }

    // --- Address availability -------------------------------------------------------------------------

    [Fact]
    public async Task AddressAvailability_ReportsAvailableTakenReservedAndInvalid()
    {
        using var taken = Client();
        var takenSlug = SignupDraftClient.NewSlug();
        await taken.RegisterAsync(takenSlug);
        using var browser = _host.Web.NewBrowser();

        async Task<(string Slug, string Status)> CheckAsync(string slug)
        {
            var response = await browser.GetAsync($"/signup/address-availability?slug={Uri.EscapeDataString(slug)}");
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Contains("no-store", response.CacheControl, StringComparison.OrdinalIgnoreCase);
            return (response.Json.GetProperty("slug").GetString()!, response.Json.GetProperty("status").GetString()!);
        }

        var free = SignupDraftClient.NewSlug();
        Assert.Equal((free, "available"), await CheckAsync($"  {free.ToUpperInvariant()} "));
        Assert.Equal((takenSlug, "taken"), await CheckAsync(takenSlug));
        Assert.Equal(("admin", "taken"), await CheckAsync("admin"));
        Assert.Equal("invalid", (await CheckAsync("bad slug!")).Status);
        Assert.Equal("invalid", (await CheckAsync("")).Status);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static string Wrong(string code) =>
        ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertError(TestResponse response, HttpStatusCode status, string code)
    {
        Assert.True(response.Status == status, $"Expected {(int)status} {code}, got {(int)response.Status} {SignupDraftClient.CodeOf(response)}");
        Assert.Equal(code, response.Json.GetProperty("code").GetString());
    }

    private static void AssertFieldError(TestResponse response, string section, string field, string code)
    {
        AssertError(response, HttpStatusCode.BadRequest, "Signup.Draft.ValidationFailed");
        var errors = response.Json.GetProperty("fieldErrors").EnumerateArray()
            .Select(e => (e.GetProperty("section").GetString(), e.GetProperty("field").GetString(), e.GetProperty("code").GetString()))
            .ToList();
        Assert.Contains((section, field, code), errors);
    }

    private async Task<string> StoredHashAsync(SignupDraftClient client)
    {
        await using var db = _host.Db();
        return (await db.RegistrationDrafts.AsNoTracking()
            .SingleAsync(d => d.OwnerMobilePhone == client.MobileE164, TestContext.Current.CancellationToken)).PasswordHash!;
    }

    private async Task<int> DraftCountForAsync(SignupDraftClient client)
    {
        await using var db = _host.Db();
        return await db.RegistrationDrafts.CountAsync(d => d.OwnerMobilePhone == client.MobileE164, TestContext.Current.CancellationToken);
    }
}

/// <summary>A clock the test moves forward; every server read of the time sees it.</summary>
public sealed class SignupTestClock(DateTime startUtc) : TimeProvider
{
    private long _ticks = startUtc.Ticks;

    public override DateTimeOffset GetUtcNow() => new(new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
