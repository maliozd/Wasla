using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Contact;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Signup;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Contact;

/// <summary>
/// The public contact page over real HTTP (<see cref="SignupWebHost"/>: the real controller, compiled views, antiforgery,
/// localization and the production rate-limit registration) with an in-memory CentralDb. A stored message redirects to
/// the thank-you state; invalid input, the hidden field and the rate limit store nothing. Fake data only.
/// </summary>
public sealed partial class ContactPageWebTests : IAsyncLifetime
{
    public static readonly TheoryData<string> Cultures = new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-contact-keys-").FullName;
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

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Page_RendersTheFormAndChannels_InEveryCulture(string culture)
    {
        var host = await StartAsync();
        using var browser = Browser(host, culture);

        var page = await browser.GetAsync(PublicSiteUrls.ContactPath);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var dir = culture == "ar-SA" ? "rtl" : "ltr";
        Assert.Contains($"<html lang=\"{culture}\" dir=\"{dir}\"", page.Body);
        Assert.Contains($"<title>{Encoded(Text(culture, "Marketing.Contact.MetaTitle"))}</title>", page.Body);
        Assert.Contains("<link rel=\"canonical\" href=\"http://wasla.local/contact\" />", page.Body);
        Assert.Single(Regex.Matches(page.Body, "<h1[ >]"));
        Assert.Contains(Encoded(Text(culture, "Marketing.Contact.Title")), page.Body);
        Assert.Contains("name=\"__RequestVerificationToken\"", page.Body);
        var decoded = WebUtility.HtmlDecode(page.Body);
        Assert.Contains($"href=\"mailto:{PublicSiteUrls.ContactEmail}\"", decoded);
        Assert.Contains($"href=\"{PublicSiteUrls.ContactPhoneHref}\"", decoded);
        Assert.Contains(PublicSiteUrls.ContactPhoneDisplay, decoded);
        Assert.Contains($"href=\"{PublicSiteUrls.InstagramUrl}\"", decoded);
        Assert.Contains($"href=\"{PublicSiteUrls.PrivacyNoticePath}\"", decoded);

        // Every form control has a label pointing at its id, and the hidden field is out of reach.
        foreach (var id in new[] { "contact-full-name", "contact-restaurant-name", "contact-email", "contact-phone", "contact-message", "contact-privacy", "contact-website" })
        {
            Assert.Contains($"id=\"{id}\"", page.Body);
            Assert.Contains($"for=\"{id}\"", page.Body);
        }

        foreach (var topic in Enum.GetNames<ContactTopic>())
            Assert.Contains($"for=\"contact-topic-{topic}\"", page.Body);
        Assert.Matches("class=\"wm-visually-hidden wm-contact-form__trap\" aria-hidden=\"true\"", page.Body);
        Assert.Matches("id=\"contact-website\"[^>]*tabindex=\"-1\"", page.Body);
        Assert.DoesNotContain("data-contact-state=\"sent\"", page.Body);
    }

    [Fact]
    public async Task ValidMessage_IsStored_ThenThePageThanksTheSender()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "en-US");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.ContactPath);

        var posted = await browser.PostFormAsync(PublicSiteUrls.ContactPath, Form(token, ("Phone", "+90 544 669 56 91"), ("Topic", "PrintBridgeSetup")));

        Assert.Equal(HttpStatusCode.Redirect, posted.Status);
        Assert.Equal(PublicSiteUrls.ContactPath, posted.Location);
        await using (var db = NewContext())
        {
            var stored = await db.ContactMessages.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Ayşe Yılmaz", stored.FullName);
            Assert.Equal("Lezzet Ocakbaşı", stored.RestaurantName);
            Assert.Equal("ayse@lezzet.test", stored.Email);
            Assert.Equal("5446695691", stored.Phone);
            Assert.Equal(ContactTopic.PrintBridgeSetup, stored.Topic);
            Assert.Equal("en-US", stored.Culture);
            Assert.True(stored.PrivacyNoticeAcknowledged);
            // The test host keeps Email:Provider at Log: nothing is sent, and the message says so.
            Assert.Equal(ContactMessageNotificationStatus.NotConfigured, stored.NotificationStatus);
        }

        var thanks = await browser.GetAsync(PublicSiteUrls.ContactPath);
        Assert.Equal(HttpStatusCode.OK, thanks.Status);
        Assert.Contains("data-contact-state=\"sent\"", thanks.Body);
        Assert.Contains(Encoded(Text("en-US", "Contact.Sent.Title")), thanks.Body);
        Assert.DoesNotContain("id=\"contact-form\"", thanks.Body);

        // The thank-you state is shown once; a reload shows the form again.
        var reload = await browser.GetAsync(PublicSiteUrls.ContactPath);
        Assert.Contains("id=\"contact-form\"", reload.Body);
    }

    [Fact]
    public async Task InvalidMessage_IsShownAgainWithItsErrors_AndNothingIsStored()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "tr-TR");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.ContactPath);

        var page = await browser.PostFormAsync(PublicSiteUrls.ContactPath,
        [
            new("__RequestVerificationToken", token),
            new("FullName", ""),
            new("RestaurantName", "Lezzet <b>Ocak</b>"),
            new("Email", "not-an-address"),
            new("Phone", "123"),
            new("Topic", "Catering"),
            new("Message", "")
        ]);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var html = WebUtility.HtmlDecode(page.Body);
        foreach (var key in new[]
                 {
                     "Validation.FullNameRequired", "Validation.EmailInvalid", "Validation.ContactPhoneInvalid",
                     "Validation.ContactTopicInvalid", "Validation.ContactMessageRequired", "Validation.PrivacyNoticeRequired"
                 })
            Assert.Contains(Text("tr-TR", key), html, StringComparison.Ordinal);
        Assert.DoesNotContain("is not valid", html, StringComparison.Ordinal);
        Assert.Contains("aria-invalid=\"true\"", page.Body);
        // What the visitor typed is kept, encoded.
        Assert.Contains("value=\"Lezzet &lt;b&gt;Ocak&lt;/b&gt;\"", page.Body);
        Assert.DoesNotContain("<b>Ocak</b>", page.Body);
        Assert.False(await AnyStoredAsync());
    }

    [Fact]
    public async Task FilledHiddenField_LooksSent_ButStoresNothing()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "tr-TR");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.ContactPath);

        var posted = await browser.PostFormAsync(PublicSiteUrls.ContactPath, Form(token, ("Website", "https://spam.example")));

        Assert.Equal(HttpStatusCode.Redirect, posted.Status);
        Assert.Equal(PublicSiteUrls.ContactPath, posted.Location);
        Assert.False(await AnyStoredAsync());
        Assert.Contains("data-contact-state=\"sent\"", (await browser.GetAsync(PublicSiteUrls.ContactPath)).Body);
    }

    [Fact]
    public async Task PostWithoutAnAntiforgeryToken_IsRejected()
    {
        var host = await StartAsync();
        using var browser = Browser(host, "tr-TR");

        var posted = await browser.PostFormAsync(PublicSiteUrls.ContactPath, Form(token: null));

        Assert.Equal(HttpStatusCode.BadRequest, posted.Status);
        Assert.False(await AnyStoredAsync());
    }

    [Fact]
    public async Task SubmissionsOverTheNetworkLimit_Get429WithTheFormAgain_AndAreNotStored()
    {
        var host = await StartAsync(new Dictionary<string, string?> { ["AuthenticationRateLimiting:ContactSubmission:PermitLimit"] = "2" });
        using var browser = Browser(host, "en-US");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.ContactPath);

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync(PublicSiteUrls.ContactPath, Form(token))).Status);

        var limited = await browser.PostFormAsync(PublicSiteUrls.ContactPath, Form(token, ("Message", "Third message")));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.InRange(limited.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        var html = WebUtility.HtmlDecode(limited.Body);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, Text("en-US", "Contact.TooManyAttempts"), PublicSiteUrls.ContactEmail), html, StringComparison.Ordinal);
        Assert.Contains("Third message", html, StringComparison.Ordinal);
        await using var db = NewContext();
        Assert.Equal(2, await db.ContactMessages.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenStoringFails_TheFormIsShownAgainWithTheFailureBanner()
    {
        var host = await StartAsync(configureServices: services => services.AddScoped<IContactMessageService, FailingContactMessageService>());
        using var browser = Browser(host, "tr-TR");
        var token = await browser.GetAntiforgeryTokenAsync(PublicSiteUrls.ContactPath);

        var page = await browser.PostFormAsync(PublicSiteUrls.ContactPath, Form(token));

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("data-contact-state=\"failed\"", page.Body);
        var html = WebUtility.HtmlDecode(page.Body);
        Assert.Contains(Text("tr-TR", "Contact.Failed.Title"), html, StringComparison.Ordinal);
        Assert.Contains($"href=\"mailto:{PublicSiteUrls.ContactEmail}\"", page.Body);
        Assert.Contains("value=\"Ayşe Yılmaz\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-contact-state=\"sent\"", page.Body);
    }

    private async Task<SignupWebHost> StartAsync(
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        // The contact page belongs to the full marketing site, which is published here.
        var settings = new Dictionary<string, string?>(SignupWebHost.FullMarketingSite);
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key] = value;
        _host = await SignupWebHost.StartAsync(_connection, _keyRing, configuration: settings, configureServices: configureServices);
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
            ["FullName"] = "Ayşe Yılmaz",
            ["RestaurantName"] = "Lezzet Ocakbaşı",
            ["Email"] = "ayse@lezzet.test",
            ["Phone"] = "",
            ["Topic"] = "WaslaOrders",
            ["Message"] = "Trendyol GO siparişlerimizi tek ekranda toplamak istiyoruz.",
            ["PrivacyNoticeAcknowledged"] = "true",
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

    private sealed class FailingContactMessageService : IContactMessageService
    {
        public Task<ContactMessageSubmitResult> SubmitAsync(ContactMessageRequest request, CancellationToken ct) =>
            throw new DbUpdateException("CentralDb is not reachable.");
    }
}
