using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Signup;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The pilot restaurant application (WAS-217) over real HTTP (<see cref="SignupWebHost"/>: the real controllers, compiled
/// views, antiforgery, localization and rate limits) with an in-memory CentralDb, in Production unless a test says
/// otherwise. The full marketing site and public signup stay closed. Fake data and fake legal facts only; Email:Provider
/// stays Log, so nothing is sent.
/// </summary>
public sealed partial class PilotPageWebTests : IAsyncLifetime
{
    public static readonly TheoryData<string> Cultures = new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-pilot-keys-").FullName;
    private SignupWebHost? _host;

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
        await using var db = NewContext();
        db.Cities.AddRange(
            new City { Id = 34, CountryCode = "TR", Name = "İstanbul", PlateCode = "34", SortOrder = 34, IsActive = true },
            new City { Id = 6, CountryCode = "TR", Name = "Ankara", PlateCode = "06", SortOrder = 6, IsActive = true },
            new City { Id = 16, CountryCode = "TR", Name = "Bursa", PlateCode = "16", SortOrder = 16, IsActive = true },
            new City { Id = 17, CountryCode = "TR", Name = "Çanakkale", PlateCode = "17", SortOrder = 17, IsActive = true },
            new City { Id = 7, CountryCode = "TR", Name = "Antalya", PlateCode = "07", SortOrder = 7, IsActive = false });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    // ---- Closed ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ByDefault_ThePilotPagesDoNotExist_AndTheComingSoonPageKeepsItsActions()
    {
        var host = await StartAsync(enabled: false);
        using var browser = Browser(host, "tr-TR");

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(PublicSiteUrls.PilotPath)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(PublicSiteUrls.PilotPrivacyNoticePath)).Status);
        var token = host.IssueAntiforgeryToken(browser);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token))).Status);
        Assert.False(await AnyStoredAsync());

        var home = await browser.GetAsync("/");
        Assert.DoesNotContain("href=\"/pilot\"", home.Body);
        Assert.Contains($"href=\"{PublicSiteUrls.InstagramUrl}\"", home.Body);
    }

    [Fact]
    public async Task InProduction_AnEnabledFormWithoutTheNoticeFacts_StaysClosed()
    {
        var host = await StartAsync(enabled: true, completeNotice: false);
        using var browser = Browser(host, "tr-TR");

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(PublicSiteUrls.PilotPath)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(PublicSiteUrls.PilotPrivacyNoticePath)).Status);
        Assert.DoesNotContain("href=\"/pilot\"", (await browser.GetAsync("/")).Body);
    }

    // ---- Open -----------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ComingSoonPage_LeadsToTheForm_AndKeepsTheContactEmail(string culture)
    {
        var host = await StartAsync();
        using var browser = Browser(host, culture);

        var home = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, home.Status);
        Assert.Matches($"<a class=\"wm-btn wm-btn--primary\" href=\"/pilot\" data-coming-soon-cta=\"pilot\">\\s*{Regex.Escape(Encoded(Text(culture, "Marketing.ComingSoonPage.BecomePilot")))}", home.Body);
        Assert.Contains($"href=\"mailto:{PublicSiteUrls.ContactEmail}\" data-coming-soon-cta=\"contact\">{Encoded(Text(culture, "Marketing.ComingSoonPage.ContactUs"))}</a>", home.Body);
        // The rest of the full site stays closed.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(PublicSiteUrls.ContactPath)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(PublicSiteUrls.PrivacyNoticePath)).Status);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Form_HasOnlyTheAgreedFields_LabelledControls_ThePrivacyLinkBeforeSubmit_AndNoConsentBox(string culture)
    {
        var host = await StartAsync();
        using var browser = Browser(host, culture);

        var page = await browser.GetAsync(PublicSiteUrls.PilotPath);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var dir = culture == "ar-SA" ? "rtl" : "ltr";
        Assert.Contains($"<html lang=\"{culture}\" dir=\"{dir}\"", page.Body);
        Assert.Contains($"<title>{Encoded(Text(culture, "Pilot.MetaTitle"))}</title>", page.Body);
        Assert.Single(Regex.Matches(page.Body, "<h1[ >]"));
        Assert.Contains("name=\"__RequestVerificationToken\"", page.Body);
        Assert.Matches("<input type=\"hidden\" name=\"SubmissionId\" value=\"[0-9a-f-]{36}\"", page.Body);

        // The six fields, each with a visible label; no password, payment, address, plan or setup field.
        var names = FieldName().Matches(Between(page.Body, "<form", "</form>")).Select(m => m.Groups["name"].Value)
            .Where(n => n is not "__RequestVerificationToken" and not "SubmissionId").ToList();
        Assert.Equal(new[] { "RestaurantName", "CityPlateCode", "ContactName", "Phone", "Email", "Note", "Website" }, names);
        foreach (var id in new[] { "pilot-restaurant-name", "pilot-city", "pilot-contact-name", "pilot-phone", "pilot-email", "pilot-note", "pilot-website" })
            Assert.Contains($"for=\"{id}\"", page.Body);
        Assert.DoesNotContain("type=\"checkbox\"", page.Body);
        Assert.DoesNotContain("type=\"password\"", page.Body);
        Assert.Matches("id=\"pilot-website\"[^>]*tabindex=\"-1\"", page.Body);
        Assert.Matches("class=\"wm-phone\" dir=\"ltr\"", page.Body);

        // Only the active provinces, by Turkish name, with their plate codes as values.
        var options = CityOption().Matches(page.Body).Select(m => m.Groups["value"].Value + " " + WebUtility.HtmlDecode(m.Groups["name"].Value)).ToList();
        Assert.Equal(new[] { "06 Ankara", "16 Bursa", "17 Çanakkale", "34 İstanbul" }, options);

        // The privacy notice is linked before the submit button, opens in a new tab, and asks for no consent.
        var privacy = page.Body.IndexOf($"href=\"{PublicSiteUrls.PilotPrivacyNoticePath}\"", StringComparison.Ordinal);
        Assert.True(privacy > 0 && privacy < page.Body.IndexOf("type=\"submit\"", StringComparison.Ordinal));
        Assert.Contains(Encoded(Text(culture, "Pilot.Form.PrivacyLink")), page.Body);
        Assert.Contains($"({Encoded(Text(culture, "Common.OpensInNewTab"))})", page.Body);
        Assert.Contains("data-wm-sending-label=", page.Body);
        Assert.Contains("images/marketing/pilot-order-screen.jpg", page.Body);
        Assert.Contains(Encoded(string.Format(CultureInfo.InvariantCulture, Text(culture, "Marketing.PhotoCredit"), "iMin Technology")), page.Body);
    }

    [Fact]
    public async Task ValidApplication_IsStored_ThenThePageSaysItWasReceived_WithoutPromisingAnAccountOrTime()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "tr-TR");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.PilotPath);
        var submission = Guid.NewGuid();

        var posted = await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token, ("SubmissionId", submission.ToString())));

        Assert.Equal(HttpStatusCode.Redirect, posted.Status);
        Assert.Equal(PublicSiteUrls.PilotPath, posted.Location);
        await using (var db = NewContext())
        {
            var stored = await db.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(submission, stored.Id);
            Assert.Equal(ContactTopic.PilotApplication, stored.Topic);
            Assert.Equal(("Lezzet Ocakbaşı", "Ayşe Kaya", "5324182790", "34"), (stored.RestaurantName, stored.FullName, stored.Phone, stored.CityPlateCode));
            Assert.Equal("ayse@lezzet.test", stored.Email);
            Assert.Equal("tr-TR", stored.Culture);
            Assert.False(stored.PrivacyNoticeAcknowledged);
            // Email:Provider is Log here: nothing was sent, and the row says so.
            Assert.Equal(ContactMessageNotificationStatus.NotConfigured, stored.NotificationStatus);
            // No signup, payment or provisioning.
            Assert.False(await db.PendingRegistrations.AnyAsync(TestContext.Current.CancellationToken));
            Assert.False(await db.RegistrationDrafts.AnyAsync(TestContext.Current.CancellationToken));
            Assert.False(await db.Tenants.AnyAsync(TestContext.Current.CancellationToken));
        }

        var received = await browser.GetAsync(PublicSiteUrls.PilotPath);
        Assert.Contains("data-pilot-state=\"sent\"", received.Body);
        Assert.Contains($"<title>{Encoded(Text("tr-TR", "Pilot.MetaTitleSent"))}</title>", received.Body);
        Assert.Contains(Encoded(Text("tr-TR", "Pilot.Sent.Title")), received.Body);
        Assert.Contains(Encoded(Text("tr-TR", "Pilot.Sent.NoAccount")), received.Body);
        Assert.DoesNotContain("id=\"pilot-form\"", received.Body);
        Assert.Matches("<h2 class=\"wm-pilot-sent__title\" id=\"pilot-sent-title\" tabindex=\"-1\" data-wm-focus-on-load>", received.Body);

        // The received state is shown once; a reload shows a fresh form with a new submission id.
        var reload = await browser.GetAsync(PublicSiteUrls.PilotPath);
        Assert.Contains("id=\"pilot-form\"", reload.Body);
        Assert.DoesNotContain(submission.ToString(), reload.Body);
    }

    [Fact]
    public async Task TheSameFormPostedTwice_StoresOneApplication()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "en-US");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.PilotPath);
        var form = Form(token, ("SubmissionId", Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync(PublicSiteUrls.PilotPath, form)).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync(PublicSiteUrls.PilotPath, form)).Status);

        await using var db = NewContext();
        Assert.Equal(1, await db.ContactMessages.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task InvalidApplication_ShowsTheSummaryAndFieldErrors_KeepsWhatWasTyped_AndStoresNothing(string culture)
    {
        var host = await StartAsync();
        using var browser = Browser(host, culture);
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.PilotPath);
        var submission = Guid.NewGuid().ToString();

        var page = await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token,
            ("SubmissionId", submission), ("RestaurantName", "Lezzet <b>Ocak</b>"), ("CityPlateCode", "07"),
            ("ContactName", ""), ("Phone", "532 12"), ("Email", "not-an-address"), ("Note", "Öğleden sonra arayın")));

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("data-pilot-state=\"invalid\"", page.Body);
        Assert.Contains($"<title>{Encoded(Text(culture, "Pilot.MetaTitleWithErrors"))}</title>", page.Body);
        var summary = Between(page.Body, "id=\"pilot-error-summary\"", "</ul>");
        Assert.Contains("data-wm-focus-on-load", summary);
        // The summary lists the errors in field order and links each to its field; the same sentence is under the field.
        var links = SummaryLink().Matches(summary).Select(m => m.Groups["id"].Value).ToList();
        Assert.Equal(new[] { "pilot-city", "pilot-contact-name", "pilot-phone", "pilot-email" }, links);
        var html = WebUtility.HtmlDecode(page.Body);
        foreach (var key in new[] { "Validation.PilotCityRequired", "Validation.PilotContactNameRequired", "Validation.ContactPhoneInvalid", "Validation.EmailInvalid" })
            Assert.Equal(2, Regex.Matches(html, Regex.Escape(Text(culture, key))).Count);
        Assert.Matches("id=\"pilot-phone\"[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"pilot-phone-hint pilot-phone-error\"", page.Body);
        Assert.DoesNotContain("is not valid", html, StringComparison.Ordinal);

        // What the visitor typed is kept and encoded, with the same submission id.
        Assert.Contains("value=\"Lezzet &lt;b&gt;Ocak&lt;/b&gt;\"", page.Body);
        Assert.DoesNotContain("<b>Ocak</b>", page.Body);
        Assert.Contains("value=\"532 12\"", page.Body);
        Assert.Contains("Öğleden sonra arayın", html, StringComparison.Ordinal);
        Assert.Contains($"value=\"{submission}\"", page.Body);
        Assert.False(await AnyStoredAsync());
    }

    [Fact]
    public async Task FilledHiddenField_LooksReceived_ButStoresNothing()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "tr-TR");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.PilotPath);

        var posted = await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token, ("Website", "https://spam.example")));

        Assert.Equal(HttpStatusCode.Redirect, posted.Status);
        Assert.False(await AnyStoredAsync());
        Assert.Contains("data-pilot-state=\"sent\"", (await browser.GetAsync(PublicSiteUrls.PilotPath)).Body);
    }

    [Fact]
    public async Task PostWithoutAnAntiforgeryToken_IsRejected()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "tr-TR");

        var posted = await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token: null));

        Assert.Equal(HttpStatusCode.BadRequest, posted.Status);
        Assert.False(await AnyStoredAsync());
    }

    [Fact]
    public async Task ApplicationsOverTheNetworkLimit_Get429WithTheFormAgain_AndAreNotStored()
    {
        var host = await StartAsync(configuration: new Dictionary<string, string?> { ["AuthenticationRateLimiting:PilotApplication:PermitLimit"] = "2" });
        using var browser = Browser(host, "en-US");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.PilotPath);

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token, ("SubmissionId", Guid.NewGuid().ToString())))).Status);

        var limited = await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token, ("SubmissionId", Guid.NewGuid().ToString()), ("RestaurantName", "Third Restaurant")));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.InRange(limited.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        var html = WebUtility.HtmlDecode(limited.Body);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, Text("en-US", "Pilot.TooManyAttempts"), PublicSiteUrls.ContactEmail), html, StringComparison.Ordinal);
        Assert.Contains("Third Restaurant", html, StringComparison.Ordinal);
        await using var db = NewContext();
        Assert.Equal(2, await db.ContactMessages.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenStoringFails_TheFormIsShownAgainWithTheSaveFailure_NotTheReceivedState()
    {
        var host = await StartAsync(configureServices: services => services.AddScoped<IContactMessageService, FailingContactMessageService>());
        using var browser = Browser(host, "tr-TR");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.PilotPath);

        var page = await browser.PostFormAsync(PublicSiteUrls.PilotPath, Form(token));

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("data-pilot-state=\"failed\"", page.Body);
        Assert.Contains("role=\"alert\"", page.Body);
        var html = WebUtility.HtmlDecode(page.Body);
        Assert.Contains(Text("tr-TR", "Pilot.Failed.Title"), html, StringComparison.Ordinal);
        Assert.Contains(Text("tr-TR", "Pilot.Form.Resubmit"), html, StringComparison.Ordinal);
        Assert.Contains("value=\"Ayşe Kaya\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-pilot-state=\"sent\"", page.Body);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task PrivacyNotice_ShowsTheConfiguredFacts_AndOnlyTheseFacts(string culture)
    {
        var host = await StartAsync();
        using var browser = Browser(host, culture);

        var page = await browser.GetAsync(PublicSiteUrls.PilotPrivacyNoticePath);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("data-pilot-notice-draft=\"false\"", page.Body);
        var html = WebUtility.HtmlDecode(page.Body);
        var notice = PilotApplicationModelTests.CompleteNotice();
        foreach (var fact in new[] { notice.ControllerName, notice.ControllerAddress, notice.ControllerContact, notice.LegalBasis, notice.Transfers, notice.Retention, notice.RequestChannel })
            Assert.Contains(fact!, html, StringComparison.Ordinal);
        Assert.Contains(Text(culture, "PilotPrivacy.NoMarketing"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Text(culture, "PilotPrivacy.NotConfigured"), html, StringComparison.Ordinal);
        Assert.Contains(Text(culture, "Marketing.PrivacyNotice.Right6"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideProduction_AnIncompleteNotice_IsShownAsADraft_WithItsMissingFactsMarked()
    {
        var host = await StartAsync(completeNotice: false, environment: "Development");
        using var browser = Browser(host, "tr-TR");

        var notice = await browser.GetAsync(PublicSiteUrls.PilotPrivacyNoticePath);

        Assert.Equal(HttpStatusCode.OK, notice.Status);
        Assert.Contains("data-pilot-notice-draft=\"true\"", notice.Body);
        var html = WebUtility.HtmlDecode(notice.Body);
        Assert.Contains(Text("tr-TR", "PilotPrivacy.DraftTitle"), html, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(html, Regex.Escape(Text("tr-TR", "PilotPrivacy.NotConfigured"))).Count);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(PublicSiteUrls.PilotPath)).Status);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private async Task<SignupWebHost> StartAsync(
        bool enabled = true,
        bool completeNotice = true,
        string environment = "Production",
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?> { ["Wasla:PilotApplication:Enabled"] = enabled ? "true" : "false" };
        if (completeNotice)
        {
            var notice = PilotApplicationModelTests.CompleteNotice();
            foreach (var property in typeof(PilotPrivacyNoticeOptions).GetProperties())
                settings[$"Wasla:PilotApplication:PrivacyNotice:{property.Name}"] = property.GetValue(notice) as string;
        }

        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, environmentName: environment, configuration: settings, configureServices: configureServices);
        return _host;
    }

    private static TestBrowser Browser(SignupWebHost host, string culture)
    {
        var browser = host.NewBrowser();
        browser.SetCookie(
            SignupWebHost.CentralHost,
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        return browser;
    }

    private static List<KeyValuePair<string, string>> Form(string? token, params (string Name, string Value)[] overrides)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SubmissionId"] = Guid.NewGuid().ToString(),
            ["RestaurantName"] = "Lezzet Ocakbaşı",
            ["CityPlateCode"] = "34",
            ["ContactName"] = "Ayşe Kaya",
            ["Phone"] = "532 418 27 90",
            ["Email"] = "ayse@lezzet.test",
            ["Note"] = "Hafta içi 15.00’ten sonra aranabiliriz.",
            ["Website"] = ""
        };
        foreach (var (name, value) in overrides)
            fields[name] = value;
        if (token is not null)
            fields["__RequestVerificationToken"] = token;
        return fields.ToList();
    }

    private CentralDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private async Task<bool> AnyStoredAsync()
    {
        await using var db = NewContext();
        return await db.ContactMessages.AnyAsync(TestContext.Current.CancellationToken);
    }

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found.");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        return html[from..to];
    }

    private static string Encoded(string value) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(value);

    private static readonly Dictionary<string, Dictionary<string, string>> Resources = new();

    private static string Text(string culture, string key)
    {
        lock (Resources)
        {
            if (!Resources.TryGetValue(culture, out var values))
            {
                var file = Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource.{culture}.resx");
                values = XDocument.Load(file).Root!.Elements("data")
                    .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value, StringComparer.Ordinal);
                Resources[culture] = values;
            }

            return values[key];
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Wasla.sln was not found.");
    }

    [GeneratedRegex("<(?:input|select|textarea)[^>]*\\bname=\"(?<name>[^\"]+)\"")]
    private static partial Regex FieldName();

    [GeneratedRegex("<option value=\"(?<value>\\d{2})\"[^>]*>(?<name>[^<]+)</option>")]
    private static partial Regex CityOption();

    [GeneratedRegex("<li><a href=\"#(?<id>[a-z-]+)\">")]
    private static partial Regex SummaryLink();

    private sealed class FailingContactMessageService : IContactMessageService
    {
        public Task<ContactMessageSubmitResult> SubmitAsync(ContactMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PilotApplicationSubmitResult> SubmitPilotApplicationAsync(PilotApplicationRequest request, CancellationToken ct) =>
            throw new DbUpdateException("CentralDb is not reachable.");

        public Task<ContactNotificationResendResult> ResendNotificationAsync(Guid messageId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
