using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.PrintBridge;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// The Print Bridge manual connection: the setup page offers exactly what the desktop app's Settings screen accepts
/// (the Wasla Web Panel URL and a device token) instead of a setup code nobody can enter, issues the token through
/// the existing device service, shows it once and masked by default, and never treats a token as a connection. The
/// automatic browser-to-app flow keeps its hidden setup-session infrastructure.
/// </summary>
public sealed partial class PrintBridgeManualConnectionTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private const string RawToken = "raw-device-token-for-tests-only";
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    // Device page link ---------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetupPage_LinksToTheDevicePage_OnlyWhenItsPolicyAdmitsTheUser(bool canManageDevices)
    {
        var authorization = new RecordingAuthorization(canManageDevices);
        var controller = Controller(new RecordingDevices(), authorization: authorization);

        var view = Assert.IsType<ViewResult>(await controller.Setup(CancellationToken.None));

        var model = Assert.IsType<PrintBridgeSetupViewModel>(view.Model);
        Assert.Equal(canManageDevices, model.CanManageDevices);
        // Decided on the server, by the device pages' own policy, for the signed-in user.
        Assert.Equal(new[] { TenantPolicies.CanManagePrintBridgeDevices }, authorization.Policies);
        Assert.All(authorization.Users, user => Assert.Same(controller.User, user));
        Assert.Equal(TenantPolicies.CanManagePrintBridgeDevices,
            typeof(PrintBridgeController).GetMethod(nameof(PrintBridgeController.DeviceDetails))!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void SetupPage_RendersTheManageLinkOnlyFromTheServerDecision()
    {
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");

        // The element is not in the page at all for other users; the script only reveals a link that exists.
        Assert.Matches(new Regex(@"@if \(Model\.CanManageDevices\)\s*\{\s*<a class=""small d-none"" id=""pbManualManageDeviceLink"""), view);
        Assert.Single(Regex.Matches(view, "pbManualManageDeviceLink"));
        // Issuing a token does not depend on it.
        Assert.DoesNotMatch(new Regex(@"@if \(Model\.CanManageDevices\)\s*\{\s*<button"), view);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task GuidedPrintBridgeSection_IsOfferedOnlyWithBothCapabilities(bool canManageDevices, bool canManageDeviceSecurity, bool offered)
    {
        var permissions = new TenantNavigationPermissions(true, true, true, true, true, true, true, canManageDevices, canManageDeviceSecurity, true, CanUseGuidedSetup: true);
        var state = new FakeGuidedSetup();
        state.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        var coordinator = new GuidedSetupCoordinator(state, new FixedNavigation(permissions), new FakeSetupStatus(), new FakeDemos(), new FakePrintBridgeDevices(), new CapturingLogger());
        var controller = Controller(new RecordingDevices(), authorization: new RecordingAuthorization(canManageDevices), guidedSetup: coordinator);

        var model = Assert.IsType<PrintBridgeSetupViewModel>(Assert.IsType<ViewResult>(await controller.Setup(CancellationToken.None)).Model);

        Assert.Equal(offered, model.GuidedSetup is not null);
        Assert.Equal(canManageDevices, model.CanManageDevices);
    }

    // Token issuance ----------------------------------------------------------------------------

    [Fact]
    public async Task ManualDevice_ReturnsTheNewRawTokenOnce_ForTheCurrentTenantOnly()
    {
        var devices = new RecordingDevices();
        var controller = Controller(devices);

        var result = Assert.IsType<OkObjectResult>(await controller.CreateManualDevice(CancellationToken.None));

        var json = JsonSerializer.Serialize(result.Value);
        using var body = JsonDocument.Parse(json);
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(RawToken, body.RootElement.GetProperty("token").GetString());
        Assert.Equal(devices.CreatedId, body.RootElement.GetProperty("deviceId").GetGuid());
        Assert.Equal("Print Bridge", body.RootElement.GetProperty("deviceName").GetString());
        Assert.Equal($"/print-bridge/devices/{devices.CreatedId}", body.RootElement.GetProperty("detailsUrl").GetString());
        Assert.Single(Regex.Matches(json, Regex.Escape(RawToken)));
        // The tenant comes from server-side resolution; the name gets the automatic setup's default.
        Assert.Equal(new[] { (_tenantId, string.Empty) }, devices.Created);
        Assert.Empty(devices.Regenerated);
        // Nothing survives the response: no TempData, no redirect, no header.
        Assert.Empty(controller.TempData);
        Assert.DoesNotContain(controller.Response.Headers, header => header.Value.ToString().Contains(RawToken, StringComparison.Ordinal));
    }

    [Fact]
    public void ManualDevice_IsAnAntiforgeryPost_ForDeviceSecurityManagers_WithNoRequestSuppliedIds()
    {
        var method = typeof(PrintBridgeController).GetMethod(nameof(PrintBridgeController.CreateManualDevice))!;

        Assert.Equal("setup/manual-device", method.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Null(method.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Equal(TenantPolicies.CanManageDeviceSecurity, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.True(method.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore);
        // No tenant or device identifier can come from the request.
        Assert.Equal(new[] { typeof(CancellationToken) }, method.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(AuthSchemes.Tenant, typeof(PrintBridgeController).GetCustomAttribute<AuthorizeAttribute>()!.AuthenticationSchemes);

        // Reconnecting manually reuses the existing, equally protected regenerate action for a device of this tenant.
        var regenerate = typeof(PrintBridgeController).GetMethod(nameof(PrintBridgeController.RegenerateToken))!;
        Assert.Equal("devices/{deviceId:guid}/regenerate-token", regenerate.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(regenerate.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Equal(TenantPolicies.CanManageDeviceSecurity, regenerate.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public async Task ManualDevice_AtTheActiveDeviceLimit_ExplainsTheLimit_AndReturnsNoToken()
    {
        var controller = Controller(new RecordingDevices { AtLimit = true });

        var result = Assert.IsType<BadRequestObjectResult>(await controller.CreateManualDevice(CancellationToken.None));

        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("PrintBridge.DeviceLimitReached", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualDevice_WithoutAWebPanelUrl_CreatesNothing()
    {
        var devices = new RecordingDevices();
        var controller = Controller(devices, webPanelUrl: null);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.CreateManualDevice(CancellationToken.None));

        Assert.Contains("PrintBridge.Auto.ServerUrlUnavailable", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.Empty(devices.Created);
    }

    [Fact]
    public async Task ManualDevice_WithoutAResolvedTenant_IsNotFound_AndCreatesNothing()
    {
        var devices = new RecordingDevices();
        var controller = Controller(devices, tenant: new NoTenant());

        Assert.IsType<NotFoundResult>(await controller.CreateManualDevice(CancellationToken.None));
        Assert.Empty(devices.Created);
    }

    [Fact]
    public void ExistingTokens_CanNeverBeReadBack()
    {
        // Device reads and the setup page model carry no token, only whether one exists.
        foreach (var type in new[]
                 {
                     typeof(PrintBridgeDeviceSummaryDto), typeof(PrintBridgeDeviceDetailsDto),
                     typeof(PrintBridgeSetupViewModel), typeof(PrintBridgeSetupDeviceOptionViewModel)
                 })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType == typeof(string) && p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        }

        // The page never renders a token: the field is empty until the script fills it from the one response.
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");
        var tokenField = Tag(view, "id=\"pbManualTokenValue\"");
        Assert.DoesNotContain("value=", tokenField, StringComparison.Ordinal);
        Assert.Contains("readonly", tokenField, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"off\"", tokenField, StringComparison.Ordinal);
        Assert.DoesNotContain("Model.Token", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupPage_MasksTheTokenByDefault_BehindAnAccessibleShowHideToggle()
    {
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");
        var script = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-setup.js");
        var manual = Block(view, "id=\"pbManualConnection\"", "</section>");

        Assert.StartsWith("<input type=\"password\"", Tag(manual, "id=\"pbManualTokenValue\""), StringComparison.Ordinal);
        // A real button that names its next action and says which field it controls.
        var toggle = Block(manual, "id=\"pbManualTokenToggleBtn\"", "</button>");
        Assert.Contains("<button type=\"button\"", Tag(manual, "id=\"pbManualTokenToggleBtn\""), StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"pbManualTokenValue\"", toggle, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ms-1\" id=\"pbManualTokenToggleText\">@L[\"PrintBridge.Manual.ShowToken\"]</span>", toggle, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", toggle, StringComparison.Ordinal);
        // The label itself carries the state, so it is not also marked as a pressed toggle.
        Assert.DoesNotContain("aria-pressed", manual, StringComparison.Ordinal);
        Assert.Contains("showToken = L[\"PrintBridge.Manual.ShowToken\"].Value", view, StringComparison.Ordinal);
        Assert.Contains("hideToken = L[\"PrintBridge.Manual.HideToken\"].Value", view, StringComparison.Ordinal);

        // Every new result starts masked before the token is written; the toggle changes only the field's type.
        Assert.Matches(new Regex(@"setTokenMasked\(true\);\s*value\.value = data\.token;"), script);
        Assert.Contains("if (value) value.type = masked ? \"password\" : \"text\";", script, StringComparison.Ordinal);
    }

    // Setup page ---------------------------------------------------------------------------------

    [Fact]
    public void SetupPage_OffersNoCustomerFacingSetupCode()
    {
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");
        var script = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-setup.js");

        foreach (var removed in new[] { "pbManualSetupCode", "pbManualCopySetupCode", "SetupCode", "setupCode", "OneTimeSetupCode", "setup-code" })
        {
            Assert.DoesNotContain(removed, view, StringComparison.Ordinal);
            Assert.DoesNotContain(removed, script, StringComparison.Ordinal);
        }

        // The code only travels inside the automatic flow's app link; the page never reads or shows it.
        Assert.DoesNotContain("data.code", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.code", script, StringComparison.Ordinal);
        Assert.DoesNotContain("watchManualSession", script, StringComparison.Ordinal);

        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in new[]
                     {
                         "PrintBridge.Auto.OneTimeSetupCode", "PrintBridge.Auto.SetupCodeHelp", "PrintBridge.Auto.GenerateNewDeviceSetupCode",
                         "PrintBridge.Auto.GenerateReconnectSetupCode", "PrintBridge.Auto.OneTimeSetupCodeCreated", "PrintBridge.Auto.SetupCodeExpires",
                         "PrintBridge.Auto.CopySetupCode", "PrintBridge.Auto.ManualTokenModeHelp"
                     })
            {
                Assert.False(resources.ContainsKey(key), $"{key} is still defined in '{culture}'.");
            }
        }

        // No copy on the setup page, the guided panel or Help still asks the user for a setup code.
        var english = Load(".en-US");
        foreach (var key in KeysUsedBy(view).Concat(english.Keys.Where(k => k.StartsWith("Help.PrintBridge.", StringComparison.Ordinal) || k.StartsWith("GuidedSetup.PrintBridge.", StringComparison.Ordinal))))
        {
            Assert.DoesNotMatch(new Regex("setup code|one-time code", RegexOptions.IgnoreCase), english.GetValueOrDefault(key) ?? string.Empty);
        }
    }

    [Fact]
    public void SetupPage_ManualConnection_ShowsTheWebPanelUrlAndAOneTimeToken_WithAccessibleCopyButtons()
    {
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");
        var manual = Block(view, "id=\"pbManualConnection\"", "</section>");

        Assert.Contains("for=\"pbSetupServerUrl\">@L[\"PrintBridge.Manual.WebPanelUrlLabel\"]", manual, StringComparison.Ordinal);
        Assert.Contains("for=\"pbManualTokenValue\">@L[\"PrintBridge.Manual.DeviceTokenLabel\"]", manual, StringComparison.Ordinal);
        Assert.Contains("id=\"pbManualTokenActionBtn\"", manual, StringComparison.Ordinal);
        Assert.Contains("@L[\"PrintBridge.Manual.ShownOnce\"]", manual, StringComparison.Ordinal);
        Assert.Contains("@L[\"PrintBridge.Manual.Next\"]", manual, StringComparison.Ordinal);
        // The result (token, instructions, status) stays hidden until a token was issued.
        Assert.Contains("id=\"pbManualTokenResult\" class=\"wasla-print-bridge-manual-token-result mt-3 d-none\"", manual, StringComparison.Ordinal);
        Assert.Contains("id=\"pbManualConnectionStatus\" role=\"status\"></p>", manual, StringComparison.Ordinal);
        // Both copy buttons have accessible names; each copy is announced once, politely.
        Assert.Contains("aria-label=\"@L[\"PrintBridge.Manual.CopyWebPanelUrl\"]\"", Tag(manual, "id=\"pbSetupCopyServerUrlBtn\""), StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@L[\"PrintBridge.Manual.CopyDeviceToken\"]\"", Tag(manual, "id=\"pbManualCopyTokenBtn\""), StringComparison.Ordinal);
        Assert.Contains("class=\"visually-hidden\" id=\"pbManualCopyStatus\" role=\"status\" aria-live=\"polite\"", manual, StringComparison.Ordinal);
        // URLs and tokens read left to right in the Arabic layout too.
        Assert.Contains("dir=\"ltr\"", Tag(manual, "id=\"pbSetupServerUrl\""), StringComparison.Ordinal);
        Assert.Contains("dir=\"ltr\"", Tag(manual, "id=\"pbManualTokenValue\""), StringComparison.Ordinal);
        Assert.Contains("manualDeviceCreateUrl", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupScript_KeepsTheRawTokenInTheFieldOnly_AndNeverClaimsAConnection()
    {
        var script = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-setup.js");

        // The raw token is read from the response once and written only into the read-only field's value.
        Assert.Equal(2, Regex.Matches(script, @"data\.token").Count);
        Assert.Contains("if (!data || !data.success || !data.token) throw", script, StringComparison.Ordinal);
        Assert.Contains("value.value = data.token;", script, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "localStorage", "sessionStorage", "console.", "history.", "location.hash = ", "setAttribute(\"value\"" })
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        // The manual start event carries nothing, and the page never marks the guided section connected itself.
        Assert.Contains("document.dispatchEvent(new CustomEvent(\"wasla:print-bridge-manual-setup-started\"));", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-guided-setup-state", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/guided-setup/", script, StringComparison.Ordinal);
        // Replacing an existing device's token is always confirmed for the device the user picked.
        Assert.Matches(new Regex(@"if \(!window\.confirm\(formatMessage\(messages\.regenerateConfirm, .*?\)\)\) return;\s*url = String\(cfg\.regenerateTokenUrlTemplate"), script);
    }

    [Fact]
    public void AutomaticSetup_KeepsItsHiddenSetupSessionInfrastructure()
    {
        var script = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-setup.js");
        var controller = Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "PrintBridgeController.cs");
        var setupApi = Read("src", "Wasla.Web", "Controllers", "PrintBridgeSetupApiController.cs");
        var registrations = Read("src", "Wasla.Infrastructure", "DependencyInjection", "ServiceCollectionExtensions.cs");
        var desktopClient = Read("src", "Wasla.PrintBridge", "Services", "WaslaPrintBridgeClient.cs");

        // The browser creates a session and opens the app with its one-time code inside the link...
        Assert.Contains("fetch(cfg.sessionCreateUrl, { method: \"POST\", body: form, credentials: \"same-origin\" })", script, StringComparison.Ordinal);
        Assert.Contains("window.location.href = res.body.protocolUrl;", script, StringComparison.Ordinal);
        Assert.Contains("pollStatus(res.body.statusUrl);", script, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"setup/session\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"setup/session/{sessionId:guid}/status\")]", controller, StringComparison.Ordinal);
        Assert.Contains("$\"{ProtocolScheme}://setup?server={Uri.EscapeDataString(serverUrl)}&code={Uri.EscapeDataString(created.Code)}\"", controller, StringComparison.Ordinal);
        // ...which the desktop app exchanges and completes through the setup API.
        Assert.Contains("[HttpPost(\"exchange\")]", setupApi, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"complete\")]", setupApi, StringComparison.Ordinal);
        Assert.Contains("/api/print-bridge/setup/exchange", desktopClient, StringComparison.Ordinal);
        Assert.Contains("/api/print-bridge/setup/complete", desktopClient, StringComparison.Ordinal);
        Assert.Contains("services.AddScoped<IPrintBridgeSetupSessionService, PrintBridgeSetupSessionService>();", registrations, StringComparison.Ordinal);
        // A verified automatic completion still tells the guided panel to re-read readiness.
        Assert.Matches(new Regex(@"function notifySetupCompleted\(data\) \{\s*if \(!data \|\| data\.status !== ""Completed"" \|\| !data\.connectionVerified\) return;"), script);
    }

    // Localization -------------------------------------------------------------------------------

    [Fact]
    public void ManualConnectionCopy_ExistsInAllFiveCultures_WithMatchingPlaceholders()
    {
        var english = Load(".en-US");
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");
        var manualKeys = english.Keys.Where(k => k.StartsWith("PrintBridge.Manual.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(21, manualKeys.Length);

        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in manualKeys.Concat(["PrintBridge.RegenerateConfirm", "PrintBridge.OldTokenInvalidAfterRegenerate", "PrintBridge.Auto.ManualTokenFailed"]))
            {
                Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{key} is missing in '{culture}'.");
                Assert.Equal(Placeholders(english[key]), Placeholders(resources[key]));
            }
        }

        // Every manual-connection key the page uses exists.
        foreach (var key in KeysUsedBy(view).Where(k => k.StartsWith("PrintBridge.Manual.", StringComparison.Ordinal)))
            Assert.Contains(key, manualKeys);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void ManualConnectionCopy_UsesTheDesktopAppsOwnLabels(string culture)
    {
        var web = Load(culture);
        var desktop = LoadDesktop(culture);
        var mainForm = Read("src", "Wasla.PrintBridge", "UI", "MainForm.cs");

        // The fields the desktop Settings screen labels with these keys...
        Assert.Contains("_lblServerUrl.Text = _localizer[\"Settings.ServerUrl\"];", mainForm, StringComparison.Ordinal);
        Assert.Contains("_lblAgentToken.Text = _localizer[\"Settings.AgentToken\"];", mainForm, StringComparison.Ordinal);
        // ...carry exactly the labels the Web page shows next to the values to paste.
        Assert.Equal(desktop["Settings.ServerUrl"], web["PrintBridge.Manual.WebPanelUrlLabel"]);
        Assert.Equal(desktop["Settings.AgentToken"], web["PrintBridge.Manual.DeviceTokenLabel"]);
        // The next step names the app's real Settings tab and its Save and Test buttons.
        foreach (var label in new[] { desktop["Tab.Settings"], desktop["Button.SaveConnection"], desktop["Button.TestConnection"] })
            Assert.Contains(label, web["PrintBridge.Manual.Next"], StringComparison.Ordinal);
    }

    // Helpers ------------------------------------------------------------------------------------

    private PrintBridgeController Controller(
        RecordingDevices devices,
        string? webPanelUrl = "https://tenant.wasla.local/",
        ICurrentTenantService? tenant = null,
        IAuthorizationService? authorization = null,
        IGuidedSetupCoordinator? guidedSetup = null)
    {
        var settings = new Dictionary<string, string?>();
        if (webPanelUrl is not null)
            settings["OrderHub:CustomerWebBaseUrl"] = webPanelUrl;

        var owner = new TenantNavigationPermissions(true, true, true, true, true, true, true, true, true, true, true);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Tenant"))
        };
        return new PrintBridgeController(
            tenant ?? new FixedTenant(_tenantId),
            devices,
            new UnusedSetupSessions(),
            null!,
            new TestHostEnvironment("Development"),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new KeyLocalizer(),
            guidedSetup ?? new GuidedSetupCoordinator(new FakeGuidedSetup(), new FixedNavigation(owner), new FakeSetupStatus(), new FakeDemos(), new FakePrintBridgeDevices(), new CapturingLogger()),
            authorization ?? new RecordingAuthorization(true))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
            Url = new NoRouteUrlHelper()
        };
    }

    private static string[] Placeholders(string value) =>
        PlaceholderPattern().Matches(value).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    private static IEnumerable<string> KeysUsedBy(string source) =>
        UsedKeyPattern().Matches(source).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>The text from <paramref name="start"/> to the next <paramref name="end"/>.</summary>
    private static string Block(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'.");
        return source[from..to];
    }

    /// <summary>The whole start tag that contains <paramref name="marker"/>.</summary>
    private static string Tag(string source, string marker)
    {
        var at = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{marker}' not found.");
        var open = source.LastIndexOf('<', at);
        var close = source.IndexOf('>', at);
        return source[open..(close + 1)];
    }

    private static Dictionary<string, string> Load(string culture) =>
        LoadResx(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"));

    private static Dictionary<string, string> LoadDesktop(string culture) =>
        LoadResx(Path.Combine(Root(), "src", "Wasla.PrintBridge", "Resources", $"PrintBridgeResources{culture}.resx"));

    private static Dictionary<string, string> LoadResx(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value.Trim() ?? string.Empty, StringComparer.Ordinal);

    private static string Read(params string[] segments) => File.ReadAllText(Path.Combine([Root(), .. segments]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("L\\[\"([A-Za-z0-9_.]+)\"")]
    private static partial Regex UsedKeyPattern();

    private sealed class RecordingDevices : IPrintBridgeDeviceManagementService
    {
        public bool AtLimit { get; init; }
        public Guid CreatedId { get; } = Guid.NewGuid();
        public List<(Guid TenantId, string Name)> Created { get; } = new();
        public List<(Guid TenantId, Guid DeviceId)> Regenerated { get; } = new();

        public Task<GeneratePrintBridgeTokenResult> CreateDeviceAsync(Guid customerId, string deviceName, CancellationToken ct)
        {
            if (AtLimit)
                throw new PrintBridgeDeviceLimitReachedException();
            Created.Add((customerId, deviceName));
            return Task.FromResult(new GeneratePrintBridgeTokenResult(CreatedId, RawToken, "Print Bridge"));
        }

        public Task<GeneratePrintBridgeTokenResult> RegenerateTokenAsync(Guid customerId, Guid deviceId, CancellationToken ct)
        {
            Regenerated.Add((customerId, deviceId));
            return Task.FromResult(new GeneratePrintBridgeTokenResult(deviceId, RawToken, "Print Bridge"));
        }

        public Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(Guid customerId, CancellationToken ct) => Task.FromResult<IReadOnlyList<PrintBridgeDeviceSummaryDto>>([]);
        public Task<PrintBridgeDeviceDetailsDto?> GetDeviceDetailsAsync(Guid customerId, Guid deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid customerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SetDeviceActiveAsync(Guid customerId, Guid deviceId, bool isActive, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemovePrintBridgeDeviceResult> RemoveDeviceAsync(Guid customerId, Guid deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RenamePrintBridgeDeviceResult> UpdateDeviceNameAsync(Guid customerId, Guid deviceId, string deviceName, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The manual connection never touches setup sessions.</summary>
    private sealed class UnusedSetupSessions : IPrintBridgeSetupSessionService
    {
        public Task<PrintBridgeSetupSessionCreated> CreateSessionAsync(Guid tenantId, PrintBridgeSetupMode setupMode, Guid? deviceId, string serverUrl, string? defaultDeviceName, bool confirmReplaceActiveToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrintBridgeSetupExchangeResult?> ExchangeAsync(string rawCode, PrintBridgeSetupClientInfo clientInfo, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(Guid sessionId, string completionCredential, bool connectionVerified, CancellationToken ct) => throw new NotSupportedException();
        public Task<PrintBridgeSetupStatusDto?> GetStatusAsync(Guid tenantId, Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Answers every policy check the same way and records what was asked, for whom.</summary>
    private sealed class RecordingAuthorization(bool succeeds) : IAuthorizationService
    {
        public List<string> Policies { get; } = new();
        public List<ClaimsPrincipal> Users { get; } = new();

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            throw new NotSupportedException("Only named policies are evaluated.");

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Policies.Add(policyName);
            Users.Add(user);
            return Task.FromResult(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
    }

    private sealed class NoTenant : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant => null;
    }

    /// <summary>No routes in a unit test: every generated URL falls back to the action's literal path.</summary>
    private sealed class NoRouteUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
