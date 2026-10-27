using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.UnitTests.Signup;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The public marketing site (WAS-155, refined direction) rendered by the real controllers, layout, partials and
/// resources: the homepage, the Wasla POS page, the contact page and the privacy notice in every culture, their search
/// metadata, and where every link leads.
/// </summary>
public sealed partial class PublicMarketingSiteTests : IClassFixture<PublicMarketingSiteTests.Host>
{
    public static readonly TheoryData<string> Cultures = new() { "tr-TR", "en-US", "ar-SA", "ru-RU" };

    private static readonly string[] MarketingPaths = ["/", PublicSiteUrls.PosPath, PublicSiteUrls.ContactPath, PublicSiteUrls.PrivacyNoticePath];

    /// <summary>The pages with decorative product mockups.</summary>
    private static readonly string[] MockupPaths = ["/", PublicSiteUrls.PosPath];

    /// <summary>Sign-in on the configured application entry (WAS-66): the host's Development domains, no port in Production.</summary>
    private const string ApplicationLoginUrl = "http://app.wasla.local/auth/login";

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
    public async Task Homepage_ShowsNoComingSoonOrIntegrationStatus_AndLabelsThePosScreenAsConcept(string culture)
    {
        var page = await GetAsync("/", culture);

        // The user's decision for the refined direction: no "coming soon" next to Wasla POS (its section, the header and
        // the footer) and no status for the platform integrations; the POS screen keeps its concept label. The word
        // itself may appear in unrelated copy (the Arabic billing note), so only those parts are checked.
        var soon = Encoded(Text(culture, "Marketing.ComingSoon"));
        Assert.DoesNotContain(soon, Between(page.Body, "<section class=\"wm-section wm-section--peach wm-pos-teaser\" id=\"pos\"", "</section>"));
        Assert.DoesNotContain(soon, Between(page.Body, "<header class=\"wm-header", "</header>"));
        Assert.DoesNotContain(soon, Between(page.Body, "<footer class=\"wm-footer\"", "</footer>"));
        Assert.DoesNotContain("wm-soon", page.Body);
        Assert.Contains(Encoded(Text(culture, "Marketing.ConceptUiLabel")), page.Body);
        Assert.Contains(Encoded(Text(culture, "Marketing.Platform.MigrosYemek")), page.Body);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task PrivacyNotice_HasItsMetadataOneHeadingAndTheApplicationAddress(string culture)
    {
        var page = await GetAsync(PublicSiteUrls.PrivacyNoticePath, culture);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        AssertDocument(page.Body, culture, "Marketing.PrivacyNotice.MetaTitle", "Marketing.PrivacyNotice.MetaDescription", "http://wasla.local/privacy-notice");
        Assert.Contains(Encoded(Text(culture, "Marketing.PrivacyNotice.ControllerText")), page.Body);
        Assert.Contains($"href=\"mailto:{PublicSiteUrls.ContactEmail}\"", page.Body);
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
    public async Task CallsToAction_LeadToSignupSignInPosPricingAndContact()
    {
        var home = await GetAsync("/", "tr-TR");
        var pos = await GetAsync(PublicSiteUrls.PosPath, "tr-TR");
        var homeLinks = Links(home.Body);

        Assert.Contains("/signup", homeLinks);
        Assert.Contains("/signup?plan=Starter", homeLinks);
        Assert.Contains("/signup?plan=Pro", homeLinks);
        Assert.Contains("/signup?plan=ProPlus", homeLinks);
        // The header's navigation, the header's narrow-screen link next to the menu button, and the footer.
        Assert.Equal(3, homeLinks.Count(l => l == ApplicationLoginUrl));
        Assert.DoesNotContain("/auth/login", homeLinks);
        Assert.Contains(PublicSiteUrls.PosPath, homeLinks);
        Assert.Contains("/#pricing", homeLinks);
        Assert.Contains("#how-it-works", homeLinks);
        // "Bize ulaşın" in the closing call to action and the footer, and the contact-sales plan, lead to the contact page.
        Assert.True(homeLinks.Count(l => l == PublicSiteUrls.ContactPath) >= 3);
        Assert.Contains("/#orders", Links(pos.Body));
        Assert.Contains(PublicSiteUrls.ContactPath, Links(pos.Body));
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
            foreach (var link in Links(body).Where(l => !l.StartsWith("https://", StringComparison.Ordinal)
                                                      && !l.StartsWith("mailto:", StringComparison.Ordinal)
                                                      && !l.StartsWith("tel:", StringComparison.Ordinal)))
            {
                if (link == ApplicationLoginUrl)
                {
                    // The application entry asks for the tenant address on its own host.
                    var entry = await browser.GetAsync("/auth/login", "app.wasla.local");
                    Assert.Equal(HttpStatusCode.Redirect, entry.Status);
                    Assert.True(IsExpectedRedirect("/auth/login", entry.Location), $"{link} on {path} redirected to {entry.Location}.");
                    continue;
                }

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
    public async Task InstagramAndTheApplicationEntry_AreTheOnlyAbsoluteLinks_AndInstagramShowsItsHandle(string culture)
    {
        foreach (var path in MarketingPaths)
        {
            var page = await GetAsync(path, culture);

            var external = Links(page.Body).Where(l => l.StartsWith("http", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { ApplicationLoginUrl, PublicSiteUrls.InstagramUrl }, external);
            Assert.Equal("https://www.instagram.com/app.wasla.tr/", PublicSiteUrls.InstagramUrl);
            Assert.Contains("</span>@app.wasla.tr</span>", page.Body);
        }
    }

    [Fact]
    public void ContactDetails_AreTheConfirmedAddressAndNumber()
    {
        Assert.Equal("mehmetaliozdemir@wasla.tr", PublicSiteUrls.ContactEmail);
        Assert.Equal("+90 544 669 56 91", PublicSiteUrls.ContactPhoneDisplay);
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
                    || source.StartsWith("/images/brand/wasla-app-icon.svg", StringComparison.Ordinal)
                    || source.StartsWith("/images/platforms/", StringComparison.Ordinal)
                    || source.StartsWith("/images/marketing/", StringComparison.Ordinal),
                    $"{path}: unexpected image {source}");
                Assert.Contains(" alt=\"", image.Value);
            }

            var mockups = MockupTag().Matches(page.Body);
            if (MockupPaths.Contains(path))
                Assert.NotEmpty(mockups);
            Assert.All(mockups, m => Assert.Contains("aria-hidden=\"true\"", m.Value));

            Assert.DoesNotContain("figma.com", page.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MarketingImages_ExistInTheRepository()
    {
        foreach (var file in new[]
                 {
                     "images/brand/wasla-app-icon.svg", "images/platforms/migrosyemek.svg",
                     "images/marketing/hero-kitchen-pass.jpg", "images/marketing/hero-kitchen-pass-m.jpg",
                     "images/marketing/ocakbasi-grill.jpg", "images/marketing/doner-kitchen.jpg", "images/marketing/lokanta-cook-m.jpg",
                     "images/marketing/kitchen-pass-heat-lamps-m.jpg", "images/marketing/print-bridge-overview.png",
                     "images/marketing/print-bridge-overview-narrow.png"
                 })
        {
            Assert.True(File.Exists(Path.Combine(Root(), "src", "Wasla.Web", "wwwroot", file)), file);
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
    public void CanonicalUrl_IsTheConfiguredApex_WhateverHostAndPortTheRequestNames()
    {
        var production = new TestHostEnvironment("Production");
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("WWW.Wasla.tr", 8443);
        request.QueryString = new QueryString("?ref=1");

        Assert.Equal("https://wasla.tr/pos", PublicSiteUrls.BuildCanonicalUrl(request, production, "wasla.tr", "/pos"));

        request.Host = new HostString("evil.example");
        Assert.Equal("https://wasla.tr/", PublicSiteUrls.BuildCanonicalUrl(request, production, "wasla.tr", "/"));

        // A local run keeps its own host, and Development keeps the port.
        request.Scheme = "http";
        request.Host = new HostString("localhost", 5200);
        Assert.Equal("http://localhost:5200/", PublicSiteUrls.BuildCanonicalUrl(request, new TestHostEnvironment("Development"), "wasla.local", "/"));
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
        _ when path.StartsWith("/culture/set", StringComparison.Ordinal) => location is not null && MarketingPaths.Contains(location),
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
