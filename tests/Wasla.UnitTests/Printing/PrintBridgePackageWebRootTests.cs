using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.PrintBridge;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-128: the Print Bridge package is resolved from <c>OrderHub:PrintBridgeDownload:PackagePath</c> when it is set,
/// otherwise from the web root. Web started without a web root (ASP.NET Core leaves <c>WebRootPath</c> null when the
/// content root has no <c>wwwroot</c>) must report the default package as unavailable: the setup page shows its
/// unavailable state and the download returns 404, never a 500 or a filesystem path. Packages are stand-in files.
/// </summary>
public sealed class PrintBridgePackageWebRootTests : IDisposable
{
    private const string PackageFileName = PrintBridgePackagePaths.DefaultPackageFileName;
    private const string DownloadUrl = "/print-bridge/download/package";
    private const string PackagePathKey = "OrderHub:PrintBridgeDownload:PackagePath";

    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("wasla-was128-");

    public void Dispose() => _temp.Delete(recursive: true);

    // Resolver ---------------------------------------------------------------------------------------

    [Fact]
    public void ConfiguredAbsolutePath_IsUsedAsIs_WithOrWithoutAWebRoot()
    {
        var package = StandInPackage(Path.Combine(_temp.FullName, "configured"));
        var configuration = Configuration((PackagePathKey, package));

        foreach (var environment in new[] { Environment(webRoot: null), Environment(webRoot: _temp.FullName) })
        {
            Assert.Equal(package, PrintBridgePackagePaths.ResolvePackagePath(configuration, environment));
            Assert.True(PrintBridgePackagePaths.PackageExists(configuration, environment));
        }
    }

    [Fact]
    public void ConfiguredRelativePath_ResolvesAgainstTheContentRoot_WithoutAWebRoot()
    {
        var contentRoot = Path.Combine(_temp.FullName, "content");
        var package = StandInPackage(Path.Combine(contentRoot, "packages"));
        var configuration = Configuration((PackagePathKey, Path.Combine("packages", PackageFileName)));
        var environment = Environment(webRoot: null, contentRoot: contentRoot);

        Assert.Equal(package, PrintBridgePackagePaths.ResolvePackagePath(configuration, environment));
        Assert.True(PrintBridgePackagePaths.PackageExists(configuration, environment));
    }

    [Fact]
    public void DefaultPath_IsTheDownloadFolderInTheWebRoot_WithTheConfiguredFileName()
    {
        var webRoot = Path.Combine(_temp.FullName, "wwwroot");
        var folder = Path.Combine(webRoot, "downloads", "wasla-print-bridge");
        var package = StandInPackage(folder);
        var environment = Environment(webRoot);

        Assert.Equal(package, PrintBridgePackagePaths.ResolvePackagePath(Configuration(), environment));
        Assert.True(PrintBridgePackagePaths.PackageExists(Configuration(), environment));

        var renamed = Configuration(("OrderHub:PrintBridgeDownload:PackageFileName", " Custom.zip "));
        Assert.Equal(Path.Combine(folder, "Custom.zip"), PrintBridgePackagePaths.ResolvePackagePath(renamed, environment));
        Assert.False(PrintBridgePackagePaths.PackageExists(renamed, environment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoWebRoot_LeavesTheDefaultPackageUnresolved(string? webRoot)
    {
        var environment = Environment(webRoot, contentRoot: _temp.FullName);

        Assert.Null(PrintBridgePackagePaths.ResolvePackagePath(Configuration(), environment));
        Assert.False(PrintBridgePackagePaths.PackageExists(Configuration(), environment));
    }

    [Fact]
    public void MissingWebRootFolder_OrMissingPackage_IsUnavailable()
    {
        var missingWebRoot = Path.Combine(_temp.FullName, "missing-wwwroot");
        Assert.False(PrintBridgePackagePaths.PackageExists(Configuration(), Environment(missingWebRoot)));
        Assert.False(Directory.Exists(missingWebRoot));

        Assert.False(PrintBridgePackagePaths.PackageExists(Configuration(), Environment(_temp.FullName)));
        Assert.False(PrintBridgePackagePaths.PackageExists(
            Configuration((PackagePathKey, Path.Combine(_temp.FullName, "missing.zip"))), Environment(webRoot: null)));
    }

    // Download action --------------------------------------------------------------------------------

    [Fact]
    public void Download_ServesTheDefaultWebRootPackage()
    {
        var webRoot = Path.Combine(_temp.FullName, "wwwroot");
        var package = StandInPackage(Path.Combine(webRoot, "downloads", "wasla-print-bridge"));

        var file = Assert.IsType<PhysicalFileResult>(DownloadController(Configuration(), Environment(webRoot)).DownloadPackage());

        Assert.Equal(package, file.FileName);
        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal(PackageFileName, file.FileDownloadName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing-wwwroot")]
    public void Download_WithoutAUsableWebRoot_IsNotFound(string? webRoot)
    {
        var environment = Environment(
            webRoot is { Length: > 0 } ? Path.Combine(_temp.FullName, webRoot) : webRoot,
            contentRoot: _temp.FullName);

        Assert.IsType<NotFoundResult>(DownloadController(Configuration(), environment).DownloadPackage());
    }

    // Real pipeline: setup page and download ---------------------------------------------------------

    [Fact]
    public async Task WithoutAWebRoot_SetupShowsThePackageUnavailable_AndTheDownloadIsASafe404()
    {
        await using var host = await TenantSessionWebHost.StartAsync(withoutWebRoot: true);
        using var owner = await SignInAsync(host, UserRole.Owner);

        var setup = await owner.GetAsync("/print-bridge/setup");
        var html = await setup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains("id=\"pbPackagePlaceholderCard\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"pbPackageDownloadUnavailableBtn\" disabled", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"href=\"{DownloadUrl}\"", html, StringComparison.Ordinal);
        AssertNoPathDisclosure(html);

        var download = await owner.GetAsync(DownloadUrl);
        var body = await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.NotEqual("application/zip", download.Content.Headers.ContentType?.MediaType);
        AssertNoPathDisclosure(body);
        Assert.DoesNotContain("wasla-print-bridge", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PackagePath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Contains("ArgumentNullException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfiguredPackage_WithoutAWebRoot_IsOfferedAndDownloadsForTheOwner()
    {
        var package = StandInPackage(Path.Combine(_temp.FullName, "release"));
        await using var host = await TenantSessionWebHost.StartAsync(
            configuration: new Dictionary<string, string?> { [PackagePathKey] = package },
            withoutWebRoot: true);
        using var owner = await SignInAsync(host, UserRole.Owner);

        var setup = await owner.GetAsync("/print-bridge/setup");
        var html = await setup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains($"href=\"{DownloadUrl}\"", html, StringComparison.Ordinal);
        Assert.Contains(PackageFileName, html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"pbPackagePlaceholderCard\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(package, html, StringComparison.OrdinalIgnoreCase);

        var download = await owner.GetAsync(DownloadUrl);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PackageFileName, download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(await File.ReadAllBytesAsync(package, TestContext.Current.CancellationToken),
            await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_IsRefusedWithoutDeviceSecurityPermission_AndAcrossTenants()
    {
        var package = StandInPackage(Path.Combine(_temp.FullName, "release"));
        await using var host = await TenantSessionWebHost.StartAsync(
            configuration: new Dictionary<string, string?> { [PackagePathKey] = package },
            withoutWebRoot: true);

        using var anonymous = host.Client(TenantSessionWebHost.AlphaHost);
        var signedOut = await anonymous.GetAsync(DownloadUrl);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/auth/login", LocationPath(signedOut));

        foreach (var role in new[] { UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer })
        {
            using var user = await SignInAsync(host, role);
            var refused = await user.GetAsync(DownloadUrl);
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.Equal("/auth/access-denied", LocationPath(refused));
        }

        using var owner = await SignInAsync(host, UserRole.Owner);
        using var replay = owner.ReplayTo(host.BaseAddress, TenantSessionWebHost.BetaHost);
        var crossTenant = await replay.GetAsync(DownloadUrl);
        Assert.NotEqual(HttpStatusCode.OK, crossTenant.StatusCode);
        Assert.NotEqual("application/zip", crossTenant.Content.Headers.ContentType?.MediaType);
    }

    // Helpers ----------------------------------------------------------------------------------------

    private static async Task<TenantSessionClient> SignInAsync(TenantSessionWebHost host, UserRole role)
    {
        var user = host.SeedUser(host.AlphaId, $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@alpha.test", role);
        var client = host.Client(TenantSessionWebHost.AlphaHost);
        await client.LoginAsync(user.Email);
        return client;
    }

    private static string? LocationPath(HttpResponseMessage response) =>
        response.Headers.Location is { } location
            ? (location.IsAbsoluteUri ? location : new Uri(new Uri("http://tenant.invalid"), location)).AbsolutePath
            : null;

    private void AssertNoPathDisclosure(string content)
    {
        Assert.DoesNotContain(_temp.FullName, content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wasla-session-host-", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ArgumentNullException", content, StringComparison.Ordinal);
    }

    private static string StandInPackage(string folder)
    {
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, PackageFileName);
        File.WriteAllText(package, "stand-in package");
        return package;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static TestHostEnvironment Environment(string? webRoot, string contentRoot = "") =>
        new("Production") { WebRootPath = webRoot!, ContentRootPath = contentRoot };

    // DownloadPackage reads only the configuration and the host environment.
    private static PrintBridgeController DownloadController(IConfiguration configuration, IWebHostEnvironment environment) =>
        new(null!, null!, null!, null!, environment, configuration, null!, null!, null!);
}
