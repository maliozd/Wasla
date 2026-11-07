using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Printing;
using Wasla.Contracts.Printing;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web.Controllers;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-91: the setup API's move endpoints. They read the current device token and installation from the device headers
/// (never from the body), answer only over HTTPS outside Development, and map every refusal to the status and error
/// code the desktop app treats as "nothing changed", while only an unexpected server failure is a 5xx. The ordinary
/// exchange answers a computer still registered to another restaurant with its own conflict code. The service is a fake;
/// its behavior is covered in Wasla.PrintBridge.Tests.
/// </summary>
public sealed class PrintBridgeSetupTransferApiTests
{
    private const string SourceToken = "source-token-not-a-real-credential";

    [Fact]
    public async Task Preview_ReadsTheProofsFromTheDeviceHeaders_AndReturnsOnlyNames()
    {
        var installation = Guid.NewGuid();
        var setup = new FakeSetup
        {
            Preview = new PrintBridgeTransferPreview(PrintBridgeTransferOutcome.Ready, "Anadolu", "Bahçe", "https://bahce.app.wasla.tr/", DateTime.UtcNow)
        };
        var controller = Controller(setup, installation);

        var result = await controller.PreviewTransfer(new PrintBridgeSetupTransferRequest("code-not-real", "https://bahce.app.wasla.tr/"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<PrintBridgeSetupTransferPreviewResponse>(ok.Value);
        Assert.Equal((false, "Anadolu", "Bahçe", "https://bahce.app.wasla.tr/"), (body.SameTenant, body.CurrentTenantName, body.TargetTenantName, body.TargetServerUrl));
        var request = Assert.Single(setup.Requests);
        Assert.Equal((SourceToken, installation, "code-not-real", "https://bahce.app.wasla.tr/"), (request.SourceDeviceToken, request.InstallationId!.Value, request.Code, request.SetupServerUrl));
        Assert.Null(request.NewDeviceToken);
    }

    [Fact]
    public async Task Preview_ForTheSameRestaurant_SaysSoAndNothingElse()
    {
        var controller = Controller(new FakeSetup { Preview = new PrintBridgeTransferPreview(PrintBridgeTransferOutcome.SameTenant) });

        var result = await controller.PreviewTransfer(new PrintBridgeSetupTransferRequest("code-not-real", "https://anadolu.app.wasla.tr/"), CancellationToken.None);

        var body = Assert.IsType<PrintBridgeSetupTransferPreviewResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.SameTenant);
        Assert.Null(body.CurrentTenantName);
        Assert.Null(body.TargetTenantName);
    }

    [Theory]
    [InlineData(PrintBridgeTransferOutcome.InvalidOrExpired, 400, "invalid_or_expired_setup_code")]
    [InlineData(PrintBridgeTransferOutcome.SourceRejected, 401, "source_credential_rejected")]
    [InlineData(PrintBridgeTransferOutcome.PrintingInProgress, 409, "printing_in_progress")]
    [InlineData(PrintBridgeTransferOutcome.DeviceLimitReached, 409, "device_limit_reached")]
    [InlineData(PrintBridgeTransferOutcome.InstallationConflict, 409, "installation_conflict")]
    [InlineData(PrintBridgeTransferOutcome.DestinationDeviceDisabled, 409, "destination_device_disabled")]
    [InlineData(PrintBridgeTransferOutcome.SameTenant, 409, "same_tenant")]
    [InlineData(PrintBridgeTransferOutcome.Failed, 503, "transfer_failed")]
    public async Task EveryRefusedMove_HasItsStatusAndErrorCode(PrintBridgeTransferOutcome outcome, int status, string error)
    {
        var controller = Controller(new FakeSetup { Transfer = new PrintBridgeTransferResult(outcome) });

        var result = await controller.Transfer(TransferBody(), CancellationToken.None);

        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        Assert.Equal(error, ErrorCode(refused));
    }

    [Fact]
    public async Task AStoredMove_ReturnsTheSessionAndServerOnly()
    {
        var sessionId = Guid.NewGuid();
        var installation = Guid.NewGuid();
        var setup = new FakeSetup
        {
            Transfer = new PrintBridgeTransferResult(PrintBridgeTransferOutcome.Ready, sessionId, "https://bahce.app.wasla.tr/", "QA-PC", installation)
        };

        var result = await Controller(setup, installation).Transfer(TransferBody(), CancellationToken.None);

        var body = Assert.IsType<PrintBridgeSetupTransferResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new PrintBridgeSetupTransferResponse(sessionId, "https://bahce.app.wasla.tr/", "QA-PC", installation), body);
        var request = Assert.Single(setup.Requests);
        Assert.Equal(("new-token-not-real", "completion-not-real"), (request.NewDeviceToken, request.CompletionCredential));
        // No secret goes back: the app generated them and keeps them.
        Assert.DoesNotContain("token-not-real", JsonSerializer.Serialize(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideDevelopment_PlainHttpIsRefused_BeforeTheServiceIsAsked()
    {
        var setup = new FakeSetup();
        var controller = Controller(setup, https: false, environment: "Production");

        var preview = await controller.PreviewTransfer(new PrintBridgeSetupTransferRequest("code-not-real", "https://bahce.app.wasla.tr/"), CancellationToken.None);
        var transfer = await controller.Transfer(TransferBody(), CancellationToken.None);

        Assert.Equal("invalid_or_expired_setup_code", ErrorCode(Assert.IsAssignableFrom<ObjectResult>(preview)));
        Assert.Equal("invalid_or_expired_setup_code", ErrorCode(Assert.IsAssignableFrom<ObjectResult>(transfer)));
        Assert.Empty(setup.Requests);
    }

    [Fact]
    public async Task WithoutTheDeviceHeaders_TheServiceGetsNoProof()
    {
        var setup = new FakeSetup { Preview = new PrintBridgeTransferPreview(PrintBridgeTransferOutcome.SourceRejected) };
        var controller = Controller(setup, installation: null, token: null);

        var result = await controller.PreviewTransfer(new PrintBridgeSetupTransferRequest("code-not-real", "https://bahce.app.wasla.tr/"), CancellationToken.None);

        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        var request = Assert.Single(setup.Requests);
        Assert.Equal(string.Empty, request.SourceDeviceToken);
        Assert.Null(request.InstallationId);
    }

    [Fact]
    public async Task TheOrdinaryExchange_ForAComputerOfAnotherRestaurant_AnswersItsOwnConflictCode()
    {
        var controller = Controller(new FakeSetup { ExchangeThrows = new PrintBridgeSetupInstallationRegisteredToAnotherTenantException() });

        var result = await controller.Exchange(new PrintBridgeSetupExchangeRequest("code-not-real", InstallationId: Guid.NewGuid()), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("installation_registered_to_another_tenant", ErrorCode(conflict));
    }

    [Fact]
    public void TheWebPanelExplainsAComputerOfAnotherRestaurant_InEveryCulture_WithoutNamingIt()
    {
        var root = FindRoot();
        var turkish = LoadWeb(root, "SharedResource.tr-TR.resx")["PrintBridge.Auto.InstallationRegisteredToAnotherTenant"];
        foreach (var culture in new[] { "", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU" })
        {
            var value = LoadWeb(root, $"SharedResource{culture}.resx")["PrintBridge.Auto.InstallationRegisteredToAnotherTenant"];
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.Contains("Print Bridge", value, StringComparison.Ordinal);
            if (culture is ".en-US" or ".ar-SA" or ".ru-RU")
                Assert.NotEqual(turkish, value);
        }

        var controller = File.ReadAllText(Path.Combine(root, "src", "Wasla.Web", "Areas", "Tenant", "Controllers", "PrintBridgeController.cs"));
        Assert.Contains("PrintBridgeSetupFailureReasons.InstallationRegisteredToAnotherTenant =>", controller, StringComparison.Ordinal);
    }

    // Helpers ------------------------------------------------------------------------------------

    private static PrintBridgeSetupTransferRequest TransferBody() =>
        new("code-not-real", "https://bahce.app.wasla.tr/", "new-token-not-real", "completion-not-real", "QA-PC", "QA printer");

    private static PrintBridgeSetupApiController Controller(
        FakeSetup setup,
        Guid? installation = null,
        string? token = SourceToken,
        bool https = true,
        string environment = "Production")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = https ? "https" : "http";
        if (token is not null)
            http.Request.Headers["X-PrintBridge-Token"] = token;
        if (installation is { } id)
            http.Request.Headers["X-PrintBridge-Installation-Id"] = id.ToString("D");
        http.Request.Headers["X-PrintBridge-Version"] = "1.0.0.0";

        return new PrintBridgeSetupApiController(setup, new TestHostEnvironment(environment), NullLogger<PrintBridgeSetupApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static string? ErrorCode(ObjectResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement.GetProperty("error").GetString();

    private static Dictionary<string, string> LoadWeb(string root, string file) =>
        XDocument.Load(Path.Combine(root, "src", "Wasla.Web", "Resources", file)).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Wasla.sln was not found.");
    }

    private sealed class FakeSetup : IPrintBridgeSetupSessionService
    {
        public List<PrintBridgeTransferRequest> Requests { get; } = [];

        public PrintBridgeTransferPreview Preview { get; init; } = new(PrintBridgeTransferOutcome.InvalidOrExpired);

        public PrintBridgeTransferResult Transfer { get; init; } = new(PrintBridgeTransferOutcome.InvalidOrExpired);

        public Exception? ExchangeThrows { get; init; }

        public Task<PrintBridgeTransferPreview> PreviewTransferAsync(PrintBridgeTransferRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Preview);
        }

        public Task<PrintBridgeTransferResult> TransferAsync(PrintBridgeTransferRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Transfer);
        }

        public Task<PrintBridgeSetupExchangeResult?> ExchangeAsync(string rawCode, PrintBridgeSetupClientInfo clientInfo, CancellationToken ct) =>
            ExchangeThrows is { } ex ? Task.FromException<PrintBridgeSetupExchangeResult?>(ex) : Task.FromResult<PrintBridgeSetupExchangeResult?>(null);

        public Task<PrintBridgeSetupSessionCreated> CreateSessionAsync(Guid tenantId, PrintBridgeSetupMode setupMode, Guid? deviceId, string serverUrl, string? defaultDeviceName, bool confirmReplaceActiveToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAsync(Guid sessionId, string completionCredential, bool connectionVerified, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PrintBridgeSetupStatusDto?> GetStatusAsync(Guid tenantId, Guid sessionId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
