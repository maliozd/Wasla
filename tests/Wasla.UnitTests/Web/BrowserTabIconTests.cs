using System.Net;
using Microsoft.Data.Sqlite;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.Signup;

namespace Wasla.UnitTests.Web;

/// <summary>
/// The browser tab shows the Wasla app icon (the wordmark's first letter in white on the orange tile, the recipe of
/// Print Bridge's <c>WaslaAppIcon</c>) on every page: each Razor document links it through <c>_FaviconLinks</c>, and
/// <c>favicon.ico</c> is that icon, not the ASP.NET template icon. The marketing host is rendered without a web root;
/// <see cref="AdminWebHost"/> serves the real <c>wwwroot</c>, so it shows the version query and the files themselves.
/// </summary>
public sealed class BrowserTabIconTests : IAsyncLifetime
{
    private const string Partial = "@await Html.PartialAsync(\"_FaviconLinks\")";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _keyRing = Directory.CreateTempSubdirectory("wasla-tab-icon-keys-").FullName;
    private readonly CentralTestDatabase _central = new();
    private readonly RecordingTenantDbFactory _tenants = new();
    private SignupWebHost _marketing = null!;
    private AdminWebHost _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _connection.Open();
        await SignupWebHost.CreateCentralDbAsync(_connection);
        _marketing = await SignupWebHost.StartAsync(_connection, _keyRing);
        _admin = await AdminWebHost.StartAsync(_central, _tenants);
    }

    public async ValueTask DisposeAsync()
    {
        await _admin.DisposeAsync();
        await _marketing.DisposeAsync();
        _tenants.Dispose();
        _central.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_keyRing, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EveryDocument_LinksTheIcon_InItsHead()
    {
        var web = Path.Combine(Root(), "src", "Wasla.Web");
        var documents = Directory.EnumerateFiles(web, "*.cshtml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("<head>", StringComparison.Ordinal))
            .ToList();

        Assert.True(documents.Count >= 12, $"Only {documents.Count} documents with a <head> were found.");
        Assert.All(documents, file =>
        {
            var html = File.ReadAllText(file);
            var head = html[html.IndexOf("<head>", StringComparison.Ordinal)..html.IndexOf("</head>", StringComparison.Ordinal)];
            Assert.True(head.Contains(Partial, StringComparison.Ordinal), $"{Path.GetRelativePath(web, file)} does not link the tab icon.");
        });
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/signup")]
    public async Task MarketingPages_LinkTheIconAndItsSvg(string path)
    {
        using var browser = _marketing.NewBrowser();

        var page = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Matches("<link rel=\"icon\" href=\"/favicon\\.ico(\\?v=[^\"]+)?\" sizes=\"32x32\" />", page.Body);
        Assert.Matches("<link rel=\"icon\" href=\"/images/brand/wasla-app-icon\\.svg(\\?v=[^\"]+)?\" type=\"image/svg\\+xml\" />", page.Body);
    }

    [Fact]
    public async Task WithTheRealWebRoot_TheLinksCarryAVersion_AndBothFilesAreServed()
    {
        using var browser = _admin.NewBrowser();

        var page = await browser.GetAsync("/admin/login");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Matches("<link rel=\"icon\" href=\"/favicon\\.ico\\?v=[^\"]+\" sizes=\"32x32\" />", page.Body);
        Assert.Matches("<link rel=\"icon\" href=\"/images/brand/wasla-app-icon\\.svg\\?v=[^\"]+\" type=\"image/svg\\+xml\" />", page.Body);
        foreach (var asset in new[] { "/favicon.ico", "/images/brand/wasla-app-icon.svg" })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(asset)).Status);
    }

    [Fact]
    public void FaviconIco_HoldsTheAppIconAt16_32And48Pixels()
    {
        var file = File.ReadAllBytes(Path.Combine(Root(), "src", "Wasla.Web", "wwwroot", "favicon.ico"));

        Assert.Equal(0, BitConverter.ToUInt16(file, 0));
        Assert.Equal(1, BitConverter.ToUInt16(file, 2));
        Assert.Equal(3, BitConverter.ToUInt16(file, 4));
        for (var i = 0; i < 3; i++)
        {
            var entry = 6 + 16 * i;
            Assert.Equal(new[] { 16, 32, 48 }[i], file[entry]);
            var offset = BitConverter.ToInt32(file, entry + 12);
            // Each image is a PNG, as Print Bridge's WaslaAppIcon writes them.
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, file[offset..(offset + 4)]);
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Wasla.sln was not found.");
    }
}
