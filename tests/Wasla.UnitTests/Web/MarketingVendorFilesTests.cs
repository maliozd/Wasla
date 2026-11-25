using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The pinned copies of Bootstrap 5.3.3 (stylesheet) and Bootstrap Icons 1.11.3 (stylesheet and fonts) that the
/// marketing pages load from the site instead of jsDelivr (WAS-248). Each file keeps the bytes of its npm release: the
/// expected SHA-384 values were computed from the release tarballs and match the files jsDelivr serves; the Bootstrap
/// stylesheet's value is also the SRI hash of the CDN link the other public pages keep. <c>.gitattributes</c> keeps Git
/// from converting their line endings. The rendered pages are checked with
/// <see cref="AssertStylesAndScriptsComeFromTheSite"/> by <see cref="PublicMarketingSiteTests"/>,
/// <see cref="ComingSoonPageTests"/> and <c>PilotPageWebTests</c>, and in a real browser by
/// <see cref="MarketingVendorFilesBrowserTests"/>.
/// </summary>
public sealed partial class MarketingVendorFilesTests
{
    internal const string BootstrapStylesheet = "/lib/bootstrap-5.3.3/dist/css/bootstrap.min.css";
    internal const string IconsStylesheet = "/lib/bootstrap-icons-1.11.3/font/bootstrap-icons.min.css";
    internal const string IconsFont = "/lib/bootstrap-icons-1.11.3/font/fonts/bootstrap-icons.woff2";

    /// <summary>Bootstrap 5.3.3's stylesheet: the pinned copy's hash and the SRI of the CDN link the other public pages keep.</summary>
    internal const string BootstrapSha384 = "sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH";

    public static readonly TheoryData<string, string> PinnedFiles = new()
    {
        { BootstrapStylesheet, BootstrapSha384 },
        { IconsStylesheet, "sha384-XGjxtQfXaH2tnPFa9x+ruJTuLE3Aa6LhHSWRr1XeTyhezb4abCG4ccI5AkVDxqC+" },
        { IconsFont, "sha384-QV+/zNG6sFIQ/qAWRxaR4sjpF37wr046d3pTS5QlogmJfbmyeiWip4YIIGmdK4pa" },
        { "/lib/bootstrap-icons-1.11.3/font/fonts/bootstrap-icons.woff", "sha384-jiOBsoZ7OEMAq7BXRR05+D5H/5Lna7TAlXVGHhkfH68p5P1eKJTeI4KCIOfBzG/O" }
    };

    [Theory]
    [MemberData(nameof(PinnedFiles))]
    public void PinnedFile_KeepsTheBytesOfItsRelease(string path, string sha384)
    {
        var bytes = File.ReadAllBytes(TenantOperationsRulesTests.RepoFile(["src", "Wasla.Web", "wwwroot", .. path.TrimStart('/').Split('/')]));

        Assert.Equal(sha384, "sha384-" + Convert.ToBase64String(SHA384.HashData(bytes)));
    }

    /// <summary>
    /// A rendered marketing page links the site's own Bootstrap and Bootstrap Icons stylesheets and takes no stylesheet
    /// or script from another site.
    /// </summary>
    internal static void AssertStylesAndScriptsComeFromTheSite(string html, string page)
    {
        var stylesheets = LinkTag().Matches(html)
            .Where(tag => tag.Value.Contains("rel=\"stylesheet\"", StringComparison.Ordinal))
            .Select(tag => Href().Match(tag.Value).Groups["url"].Value)
            .ToList();
        var scripts = ScriptSource().Matches(html).Select(m => m.Groups["url"].Value).ToList();

        Assert.Contains(stylesheets, url => url.Split('?')[0] == BootstrapStylesheet);
        Assert.Contains(stylesheets, url => url.Split('?')[0] == IconsStylesheet);
        Assert.All(stylesheets.Concat(scripts), url => Assert.True(
            url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal),
            $"{page} loads {url} from another site."));
    }

    [GeneratedRegex("<link\\b[^>]*>")]
    private static partial Regex LinkTag();

    [GeneratedRegex("\\bhref=\"(?<url>[^\"]*)\"")]
    private static partial Regex Href();

    [GeneratedRegex("<script\\b[^>]*\\bsrc=\"(?<url>[^\"]*)\"")]
    private static partial Regex ScriptSource();
}
