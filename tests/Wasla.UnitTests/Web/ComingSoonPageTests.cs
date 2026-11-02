using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Signup;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The marketing host with its default configuration (<c>Wasla:MarketingSite:FullSiteEnabled</c> not set): the homepage
/// is the coming-soon page, the other pages of the full site do not exist, and every public page uses the header and
/// footer that link only to the homepage, sign-in, e-mail and Instagram. Rendered by the real controllers, layout,
/// partials and resources.
/// </summary>
public sealed partial class ComingSoonPageTests : IAsyncLifetime
{
    public static readonly TheoryData<string> Cultures = new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    /// <summary>Sign-in on the configured application entry (WAS-66).</summary>
    private const string ApplicationLoginUrl = "http://app.wasla.local/auth/login";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-coming-soon-keys-").FullName;
    private SignupWebHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
        _host = await SignupWebHost.StartAsync(_connection, _keyRing);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Homepage_IsTheComingSoonPage_WithItsMetadataAndOneHeading(string culture)
    {
        var page = await GetAsync("/", culture);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        var dir = culture == "ar-SA" ? "rtl" : "ltr";
        Assert.Contains($"<html lang=\"{culture}\" dir=\"{dir}\"", page.Body);
        Assert.Contains($"<title>{Encoded(Text(culture, "Marketing.ComingSoonPage.MetaTitle"))}</title>", page.Body);
        Assert.Contains($"<meta name=\"description\" content=\"{Encoded(Text(culture, "Marketing.ComingSoonPage.MetaDescription"))}\" />", page.Body);
        Assert.Contains("<link rel=\"canonical\" href=\"http://wasla.local/\" />", page.Body);

        Assert.Single(Regex.Matches(page.Body, "<h1[ >]"));
        Assert.Contains($"<h1 class=\"wm-coming-soon__title\" id=\"coming-soon-title\">{Encoded(Text(culture, "Marketing.ComingSoonPage.Title"))}</h1>", page.Body);
        Assert.Contains(Encoded(Text(culture, "Marketing.ComingSoonPage.Eyebrow")), page.Body);
        Assert.Contains(Encoded(Text(culture, "Marketing.ComingSoonPage.Lead")), page.Body);
        // The whole page lies on the photo, so the shared header and footer are the light-on-dark variants.
        Assert.Matches("<body class=\"[^\"]*\\bwm-site--coming-soon wm-on-dark\\b", page.Body);
        Assert.Contains("class=\"wm-header wm-header--soon\"", page.Body);
        Assert.Contains("class=\"wm-footer wm-footer--soon\"", page.Body);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Homepage_OffersOnlyEmailInstagramAndSignIn_AndNoPricePlatformSignupOrForm(string culture)
    {
        var page = await GetAsync("/", culture);
        var links = Links(page.Body);

        Assert.Contains($"mailto:{PublicSiteUrls.ContactEmail}", links);
        Assert.Contains(PublicSiteUrls.InstagramUrl, links);
        // Sign-in for existing restaurants, once, in the header.
        Assert.Single(links, l => l == ApplicationLoginUrl);
        Assert.All(links, l => Assert.True(
            l == "/" || l == ApplicationLoginUrl || l == PublicSiteUrls.InstagramUrl || l == $"mailto:{PublicSiteUrls.ContactEmail}"
            || l.StartsWith("/culture/set", StringComparison.Ordinal) || l == "#main-content",
            $"Unexpected link {l}."));

        Assert.DoesNotContain("<form", page.Body);
        Assert.DoesNotContain("₺", page.Body);
        Assert.DoesNotContain("wm-mock", page.Body);
        Assert.DoesNotContain("/images/platforms/", page.Body);
        foreach (var platform in new[] { "Orders.PlatformTrendyolGo", "Orders.PlatformYemeksepeti", "Orders.PlatformGetirYemek", "Marketing.Platform.MigrosYemek" })
            Assert.DoesNotContain(Encoded(Text(culture, platform)), page.Body);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task InstagramLinks_OpenInANewTab_AndTellScreenReaders(string culture)
    {
        var page = await GetAsync("/", culture);
        var note = $"({Encoded(Text(culture, "Common.OpensInNewTab"))})";

        // The page's button and the footer handle.
        var anchors = InstagramAnchor().Matches(page.Body);
        Assert.Equal(2, anchors.Count);
        Assert.All(anchors, anchor =>
        {
            Assert.Contains("target=\"_blank\"", anchor.Groups["attributes"].Value);
            Assert.Contains("rel=\"noopener noreferrer\"", anchor.Groups["attributes"].Value);
            Assert.Contains(note, anchor.Groups["content"].Value);
        });
    }

    [Theory]
    [InlineData(PublicSiteUrls.PosPath)]
    [InlineData(PublicSiteUrls.ContactPath)]
    [InlineData(PublicSiteUrls.PrivacyNoticePath)]
    public async Task PagesOfTheFullSite_DoNotExist(string path)
    {
        using var browser = _host.NewBrowser();

        var page = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, page.Status);
    }

    [Fact]
    public async Task ContactSubmission_IsRefusedAfterTheAntiforgeryCheck_AndStoresNothing()
    {
        using var browser = _host.NewBrowser();
        // An antiforgery token is not bound to a form; the signup page issues one.
        var token = await browser.GetAntiforgeryTokenAsync("/signup");

        var page = await browser.PostFormAsync(PublicSiteUrls.ContactPath, new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("FullName", "Ayşe Yılmaz"),
            new("RestaurantName", "Lezzet Ocakbaşı"),
            new("Email", "ayse@lezzet.test"),
            new("Topic", "WaslaOrders"),
            new("Message", "Bilgi almak istiyoruz."),
            new("PrivacyNoticeAcknowledged", "true")
        });

        Assert.Equal(HttpStatusCode.NotFound, page.Status);
        await using var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);
        Assert.False(await db.ContactMessages.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OtherPublicPages_UseTheComingSoonHeaderAndFooter_AndEveryLinkReachesAnExistingPage()
    {
        using var browser = _host.NewBrowser();

        foreach (var path in new[] { "/", "/signup" })
        {
            var page = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Contains("class=\"wm-header wm-header--soon\"", page.Body);
            Assert.Contains("class=\"wm-footer wm-footer--soon\"", page.Body);

            foreach (var link in Links(page.Body).Where(l => l.StartsWith('/')).Distinct())
            {
                var response = await browser.GetAsync(link);
                Assert.True(
                    response.Status == HttpStatusCode.OK
                    || (response.Status == HttpStatusCode.Redirect && link.StartsWith("/culture/set", StringComparison.Ordinal) && response.Location == path),
                    $"{link} on {path} answered {(int)response.Status} {response.Location}.");
            }
        }
    }

    [Fact]
    public async Task Photo_IsADecorativeRepositoryImage_CreditedOnThePage()
    {
        var page = await GetAsync("/", "tr-TR");

        var photo = Between(page.Body, "<div class=\"wm-soon-photo\"", "</div>");
        Assert.Contains("aria-hidden=\"true\"", photo);
        var source = Regex.Match(photo, "<img src=\"/(?<path>[^\"]+)\" alt=\"\"").Groups["path"].Value;
        Assert.Equal("images/marketing/ocakbasi-grill-wide.jpg", source);
        Assert.True(File.Exists(Path.Combine(Root(), "src", "Wasla.Web", "wwwroot", source)), source);
        Assert.Contains(Encoded(string.Format(Text("tr-TR", "Marketing.PhotoCredit"), "Hasan Hüseyin Turan")), page.Body);
    }

    private async Task<TestResponse> GetAsync(string path, string culture)
    {
        using var browser = _host.NewBrowser();
        browser.SetCookie(
            SignupWebHost.CentralHost,
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        return await browser.GetAsync(path);
    }

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found.");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        return html[from..to];
    }

    private static List<string> Links(string html) =>
        LinkHref().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups["href"].Value)).ToList();

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

    [GeneratedRegex("<a [^>]*href=\"(?<href>[^\"]*)\"")]
    private static partial Regex LinkHref();

    [GeneratedRegex("<a (?<attributes>[^>]*href=\"https://www\\.instagram\\.com/app\\.wasla\\.tr/\"[^>]*)>(?<content>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex InstagramAnchor();
}
