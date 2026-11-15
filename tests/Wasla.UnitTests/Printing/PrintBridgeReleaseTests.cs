using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Contracts.Printing;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web.Middleware;
using Wasla.Web.PrintBridge;
using WebPrintBridgeContext = Wasla.Web.PrintBridge.PrintBridgeContext;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-63: the release Wasla.Web offers to Print Bridge devices is the downloadable setup and the
/// <c>Wasla.PrintBridge-package.json</c> the packaging script writes next to it. Only a complete, signed, consistent
/// description is offered (schema 1); anything else is "no release" (204), never a half description. The setup is served
/// only for the version currently described, and both endpoints need the device token. Files here are stand-ins.
/// </summary>
public sealed class PrintBridgeReleaseTests : IDisposable
{
    private const string SetupName = PrintBridgePackagePaths.DefaultPackageFileName;
    private const string PackagePathKey = "Wasla:PrintBridgeDownload:PackagePath";
    private static readonly byte[] SetupBytes = "stand-in setup bytes"u8.ToArray();
    private const string SetupSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly PrintBridgeAuthContext Device = new()
    {
        DeviceId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        TenantName = "Lokanta Wasla",
        DeviceName = "Kasa 1"
    };

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("wasla-was63-");

    public void Dispose() => _folder.Delete(recursive: true);

    // Catalog -------------------------------------------------------------------------------------------

    [Fact]
    public void ASignedSetupWithItsMetadata_IsTheRelease()
    {
        var setup = WriteSetup();
        WriteMetadata(Metadata());

        var release = Catalog(setup).GetCurrent();

        Assert.NotNull(release);
        Assert.Equal("1.0.1", release.Version);
        Assert.Equal(SetupName, release.FileName);
        Assert.Equal(SetupBytes.Length, release.Bytes);
        Assert.Equal(SetupSha256, release.Sha256);
        Assert.Equal(setup, release.SetupPath);
    }

    [Fact]
    public void WithoutASetup_OrWithoutItsMetadata_ThereIsNoRelease()
    {
        Assert.Null(Catalog(Path.Combine(_folder.FullName, SetupName)).GetCurrent());

        var setup = WriteSetup();
        Assert.Null(Catalog(setup).GetCurrent());
    }

    public static TheoryData<string> BrokenMetadata => new()
    {
        "unsigned",
        "noSchema",
        "otherSchema",
        "badVersion",
        "versionOutOfRange",
        "otherFile",
        "badHash",
        "zeroBytes",
        "wrongSize",
        "notJson"
    };

    [Theory]
    [MemberData(nameof(BrokenMetadata))]
    public void AnUnsignedIncompleteOrInconsistentDescription_IsNoRelease(string broken)
    {
        var setup = WriteSetup();
        var metadata = Metadata();
        var file = metadata["files"]!.AsArray()[0]!;
        switch (broken)
        {
            case "unsigned": metadata["signed"] = false; break;
            case "noSchema": metadata.Remove("schemaVersion"); break;
            case "otherSchema": metadata["schemaVersion"] = 2; break;
            case "badVersion": metadata["version"] = "1.0"; break;
            case "versionOutOfRange": metadata["version"] = "256.0.0"; break;
            case "otherFile": file["name"] = "Wasla.PrintBridge-Setup-x86.exe"; break;
            case "badHash": file["sha256"] = "XYZ"; break;
            case "zeroBytes": file["bytes"] = 0; break;
            case "wrongSize": file["bytes"] = SetupBytes.Length + 1; break;
        }

        if (broken == "notJson")
            File.WriteAllText(MetadataPath, "{ not json");
        else
            WriteMetadata(metadata);

        Assert.Null(Catalog(setup).GetCurrent());
    }

    [Fact]
    public void ASupportZipConfiguredForDownload_IsNotARelease()
    {
        var zip = Path.Combine(_folder.FullName, "Wasla.PrintBridge-win-x64-support.zip");
        File.WriteAllBytes(zip, SetupBytes);
        var metadata = Metadata();
        metadata["files"]!.AsArray()[0]!["name"] = Path.GetFileName(zip);
        WriteMetadata(metadata);

        Assert.Null(Catalog(zip).GetCurrent());
    }

    // Endpoints -----------------------------------------------------------------------------------------

    [Fact]
    public void Latest_WithoutADevice_IsUnauthorized()
    {
        var controller = Controller(WriteSetup(), device: null);
        Assert.IsType<UnauthorizedResult>(controller.LatestRelease());
        Assert.IsType<UnauthorizedResult>(controller.ReleaseSetup("1.0.1"));
    }

    [Fact]
    public void Latest_WithoutARelease_IsNoContent()
    {
        var controller = Controller(Path.Combine(_folder.FullName, SetupName), Device);
        Assert.IsType<NoContentResult>(controller.LatestRelease());
    }

    [Fact]
    public void Latest_DescribesTheRelease_InSchema1()
    {
        var setup = WriteSetup();
        WriteMetadata(Metadata());

        var ok = Assert.IsType<OkObjectResult>(Controller(setup, Device).LatestRelease());
        var response = Assert.IsType<PrintBridgeReleaseResponse>(ok.Value);

        Assert.Equal(1, response.SchemaVersion);
        Assert.Equal("1.0.1", response.Version);
        Assert.Equal(new PrintBridgeReleaseSetupResponse(SetupName, SetupBytes.Length, SetupSha256), response.Setup);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.2")]
    [InlineData("..")]
    [InlineData("1.0.1.0")]
    public void Setup_OfAnotherVersion_IsNotFound(string version)
    {
        var setup = WriteSetup();
        WriteMetadata(Metadata());

        Assert.IsType<NotFoundResult>(Controller(setup, Device).ReleaseSetup(version));
    }

    [Fact]
    public void Setup_OfTheCurrentRelease_IsTheConfiguredFile()
    {
        var setup = WriteSetup();
        WriteMetadata(Metadata());

        var file = Assert.IsType<PhysicalFileResult>(Controller(setup, Device).ReleaseSetup("1.0.1"));
        Assert.Equal(setup, file.FileName);
        Assert.Equal("application/vnd.microsoft.portable-executable", file.ContentType);
    }

    [Theory]
    [InlineData("/api/print-bridge/releases/latest")]
    [InlineData("/api/print-bridge/releases/1.0.1/setup")]
    public async Task ReleaseRequests_NeedTheDeviceToken_EvenThoughTheDownloadEndsInSetup(string path)
    {
        // Only /api/print-bridge/setup/... (one-time setup codes) is exempt from the device token.
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = new PrintBridgeAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, null!, NullLogger<PrintBridgeAuthMiddleware>.Instance);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // Helpers -------------------------------------------------------------------------------------------

    private string MetadataPath => Path.Combine(_folder.FullName, PrintBridgeReleaseCatalog.MetadataFileName);

    private string WriteSetup()
    {
        var setup = Path.Combine(_folder.FullName, SetupName);
        File.WriteAllBytes(setup, SetupBytes);
        return setup;
    }

    private static JsonObject Metadata() => new()
    {
        ["schemaVersion"] = 1,
        ["product"] = "Wasla Print Bridge",
        ["version"] = "1.0.1",
        ["signed"] = true,
        ["files"] = new JsonArray(new JsonObject
        {
            ["name"] = SetupName,
            ["role"] = "setup",
            ["bytes"] = SetupBytes.Length,
            ["sha256"] = SetupSha256
        })
    };

    private void WriteMetadata(JsonObject metadata) => File.WriteAllText(MetadataPath, metadata.ToJsonString());

    private static PrintBridgeReleaseCatalog Catalog(string setupPath) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>(PackagePathKey, setupPath)])
                .Build(),
            new TestHostEnvironment("Production") { WebRootPath = null!, ContentRootPath = string.Empty },
            NullLogger<PrintBridgeReleaseCatalog>.Instance);

    private static Wasla.Web.Controllers.PrintBridgeApiController Controller(string setupPath, PrintBridgeAuthContext? device)
    {
        var http = new DefaultHttpContext();
        if (device is not null)
            WebPrintBridgeContext.Set(http, device);
        return new Wasla.Web.Controllers.PrintBridgeApiController(null!, null!, null!, Catalog(setupPath))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }
}
