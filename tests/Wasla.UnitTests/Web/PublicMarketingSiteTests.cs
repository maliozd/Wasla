using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Wasla.UnitTests.Signup;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The public marketing site (WAS-155) rendered by the real controllers, layout, partials and resources: the
/// homepage and the Wasla POS page in every culture, their search metadata, and where every link leads.
/// </summary>
public sealed partial class PublicMarketingSiteTests : IClassFixture<PublicMarketingSiteTests.Host>
{
    public static readonly TheoryData<string> Cultures = new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    private static readonly string[] MarketingPaths = ["/", PublicSiteUrls.PosPath];

    private readonly Host _host;

    public PublicMarketingSiteTests(Host host)
    {
        _host = host;
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Homepage_HasItsTitleDescriptionCanonicalAndOneHeading(string culture)
    {
        var page = await GetAsync("/", culture);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        AssertDocument(page.Body, culture, "Marketing.Home.MetaTitle", "Marketing.Home.MetaDescription", "http://wasla.local/");
        Assert.Contains(Encoded(Text(culture, "Marketing.Home.HeroTitleAccent")), page.Body);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task PosPage_SaysComingSoonAndThatItCannotBeBoughtYet(string culture)
    {
        var page = await GetAsync(PublicSiteUrls.PosPath, culture);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        AssertDocument(page.Body, culture, "Marketing.Pos.MetaTitle", "Marketing.Pos.MetaDescription", "http://wasla.local/pos");
        Assert.Contains(Encoded(Text(culture, "Marketing.ComingSoon")), Heading(page.Body, "h1", culture, before: true));
        Assert.Contains(Encoded(Text(culture, "Marketing.Pos.Notice")), page.Body);
        Assert.Contains(Encoded(Text(culture, "Marketing.ConceptUi")), page.Body);
    }

    [Theory]
    [InlineData("www.wasla.local", "/", "http://wasla.local/")]
    [InlineData("www.wasla.local", "/pos", "http://wasla.local/pos")]
    [InlineData("wasla.local", "/?utm_source=x", "http://wasla.local/")]
    public async Task CanonicalUrl_IsTheApexHostWithoutQuery(string host, string path, string canonical)
    {
        using var browser = _host.Web.NewBrowser();

        var page = await browser.GetAsync(path, host);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains($"<link rel=\"canonical\" href=\"{canonical}\" />", page.Body);
        Assert.Contains($"<meta property=\"og:url\" content=\"{canonical}\" />", page.Body);
    }

    [Fact]
    public async Task CallsToAction_LeadToSignupSignInPosAndPricing()
    {
        var home = await GetAsync("/", "tr-TR");
        var pos = await GetAsync(PublicSiteUrls.PosPath, "tr-TR");
        var homeLinks = Links(home.Body);

        Assert.Contains("/signup", homeLinks);
        Assert.Contains("/signup?plan=Starter", homeLinks);
        Assert.Contains("/signup?plan=Pro", homeLinks);
        Assert.Contains("/signup?plan=ProPlus", homeLinks);
        Assert.Contains("/auth/login", homeLinks);
        Assert.Contains(PublicSiteUrls.PosPath, homeLinks);
        Assert.Contains("#pricing", homeLinks);
        Assert.Contains("/#orders", Links(pos.Body));
        Assert.Contains(PublicSiteUrls.InstagramUrl, Links(pos.Body));
    }

    [Fact]
    public async Task EveryLink_ReachesAnExistingPageOrSection()
    {
        using var browser = _host.Web.NewBrowser();
        var pages = new Dictionary<string, string>();
        foreach (var path in MarketingPaths)
            pages[path] = (await browser.GetAsync(path)).Body;

        foreach (var (path, body) in pages)
        {
            foreach (var link in Links(body).Where(l => !l.StartsWith("https://", StringComparison.Ordinal)))
            {
                var target = link.StartsWith('#') ? path + link : link;
                var hash = target.IndexOf('#');
                var targetPath = hash < 0 ? target : target[..hash];
                var fragment = hash < 0 ? null : target[(hash + 1)..];
                if (targetPath.Length == 0)
                    targetPath = path;

                var response = await browser.GetAsync(targetPath);
                Assert.True(
                    response.Status is HttpStatusCode.OK or HttpStatusCode.Redirect,
                    $"{link} on {path} answered {(int)response.Status}.");
                if (response.Status == HttpStatusCode.Redirect)
                    Assert.True(IsExpectedRedirect(targetPath, response.Location), $"{link} on {path} redirected to {response.Location}.");

                if (fragment is not null)
                {
                    var targetBody = pages.TryGetValue(targetPath, out var known) ? known : response.Body;
                    Assert.True(targetBody.Contains($"id=\"{fragment}\"", StringComparison.Ordinal), $"{link} on {path}: no element with id {fragment}.");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Instagram_IsTheOnlyExternalLink_AndShowsItsHandle(string culture)
    {
        foreach (var path in MarketingPaths)
        {
            var page = await GetAsync(path, culture);

            var external = Links(page.Body).Where(l => l.StartsWith("http", StringComparison.Ordinal)).Distinct().ToList();
            Assert.Equal(new[] { PublicSiteUrls.InstagramUrl }, external);
            Assert.Equal("https://www.instagram.com/app.wasla.tr/", PublicSiteUrls.InstagramUrl);
            Assert.Contains("</span>app.wasla.tr</span>", page.Body);
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task MarketingPages_UseOnlyRepositoryImagesAndHideDecorativeMockups(string culture)
    {
        foreach (var path in MarketingPaths)
        {
            var page = await GetAsync(path, culture);

            foreach (Match image in ImageTag().Matches(page.Body))
            {
                var source = image.Groups["src"].Value;
                Assert.True(
                    source.StartsWith("/images/brand/wasla-logo.svg", StringComparison.Ordinal)
                    || source.StartsWith("/images/platforms/yemeksepeti.svg", StringComparison.Ordinal)
                    || source.StartsWith("/images/platforms/getiryemek.svg", StringComparison.Ordinal),
                    $"{path}: unexpected image {source}");
                Assert.Contains(" alt=\"", image.Value);
            }

            var mockups = MockupTag().Matches(page.Body);
            Assert.NotEmpty(mockups);
            Assert.All(mockups, m => Assert.Contains("aria-hidden=\"true\"", m.Value));

            Assert.DoesNotContain("figma.com", page.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Migros", page.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mailto:", page.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SignupPage_KeepsItsFormUnderTheSharedHeader()
    {
        var page = await GetAsync("/signup", "tr-TR");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("class=\"wm-header\"", page.Body);
        Assert.Contains("class=\"wm-footer\"", page.Body);
        Assert.Contains("__RequestVerificationToken", page.Body);
        Assert.DoesNotContain("rel=\"canonical\"", page.Body);
    }

    [Fact]
    public void CanonicalUrl_KeepsSchemeAndPortAndDropsWww()
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("WWW.Wasla.tr");
        request.QueryString = new QueryString("?ref=1");

        Assert.Equal("https://wasla.tr/pos", PublicSiteUrls.BuildCanonicalUrl(request, "/pos"));

        request.Scheme = "http";
        request.Host = new HostString("localhost", 5200);
        Assert.Equal("http://localhost:5200/", PublicSiteUrls.BuildCanonicalUrl(request, "/"));
    }

    private static void AssertDocument(string html, string culture, string titleKey, string descriptionKey, string canonical)
    {
        var dir = culture == "ar-SA" ? "rtl" : "ltr";
        Assert.Contains($"<html lang=\"{culture}\" dir=\"{dir}\"", html);
        Assert.Contains($"<title>{Encoded(Text(culture, titleKey))}</title>", html);
        Assert.Contains($"<meta name=\"description\" content=\"{Encoded(Text(culture, descriptionKey))}\" />", html);
        Assert.Contains($"<meta property=\"og:title\" content=\"{Encoded(Text(culture, titleKey))}\" />", html);
        Assert.Contains($"<meta property=\"og:locale\" content=\"{culture.Replace('-', '_')}\" />", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"{canonical}\" />", html);
        Assert.Single(Regex.Matches(html, "<h1[ >]"));
        Assert.Contains("<main id=\"main-content\"", html);
    }

    private static bool IsExpectedRedirect(string path, string? location) => path switch
    {
        "/auth/login" => location?.StartsWith("/tenant-address-required", StringComparison.Ordinal) == true,
        _ when path.StartsWith("/culture/set", StringComparison.Ordinal) => location is "/" or "/pos",
        _ => false
    };

    /// <summary>The text between the start of the page and its first heading of the given level, or the heading itself.</summary>
    private static string Heading(string html, string tag, string culture, bool before)
    {
        var start = html.IndexOf($"<{tag} ", StringComparison.Ordinal);
        Assert.True(start > 0, $"No {tag} ({culture}).");
        var main = html.IndexOf("<main", StringComparison.Ordinal);
        return before ? html[main..start] : html[start..html.IndexOf($"</{tag}>", start, StringComparison.Ordinal)];
    }

    private async Task<TestResponse> GetAsync(string path, string culture)
    {
        using var browser = _host.Web.NewBrowser();
        browser.SetCookie(
            SignupWebHost.CentralHost,
            CookieRequestCultureProvider.DefaultCookieName,
            Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture))));
        return await browser.GetAsync(path);
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

    [GeneratedRegex("<img [^>]*src=\"(?<src>[^\"]*)\"[^>]*>")]
    private static partial Regex ImageTag();

    [GeneratedRegex("<[a-z]+ [^>]*class=\"[^\"]*\\bwm-mock\\b[^\"]*\"[^>]*>")]
    private static partial Regex MockupTag();

    public sealed class Host : IAsyncLifetime
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-marketing-keys-").FullName;

        internal SignupWebHost Web { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _connection.Open();
            await SignupWebHost.CreateCentralDbAsync(_connection);
            Web = await SignupWebHost.StartAsync(_connection, _keyRing);
        }

        public async ValueTask DisposeAsync()
        {
            await Web.DisposeAsync();
            _connection.Dispose();
            try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
        }
    }
}
