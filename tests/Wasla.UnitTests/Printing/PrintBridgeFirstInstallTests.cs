using System.Reflection;
using System.Security.Claims;
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
/// The guided Print Bridge first install: while guided setup is at Print Bridge the setup page offers one linear path for a
/// new device (download, install, open and connect, test the printer, then the device guide) instead of the setup-type
/// choice and reconnect. The download is the existing same-origin package route and creates nothing; only "open and
/// connect" creates the hidden setup session. The ordinary page keeps its choice and reconnect.
/// </summary>
public sealed partial class PrintBridgeFirstInstallTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly FakeGuidedSetup _state = new();
    private readonly FakeSetupStatus _readiness = new();
    private readonly FakePrintBridgeDevices _devices = new();

    // Server decision -------------------------------------------------------------------------------

    [Theory]
    [InlineData(GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge, true, true, true)]
    [InlineData(GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge, false, true, false)]
    [InlineData(GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge, true, false, false)]
    [InlineData(GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections, true, true, false)]
    [InlineData(GuidedSetupStatus.NotStarted, null, true, true, false)]
    [InlineData(GuidedSetupStatus.Skipped, GuidedSetupSections.PrintBridge, true, true, false)]
    [InlineData(GuidedSetupStatus.Completed, GuidedSetupSections.PrintBridge, true, true, false)]
    public async Task FirstInstallMode_IsDecidedOnTheServer_FromTheGuidedSection(
        GuidedSetupStatus status, string? section, bool canManageDevices, bool canManageDeviceSecurity, bool expected)
    {
        _state.Set(_user, status, section);
        var permissions = Owner with { CanManagePrintBridgeDevices = canManageDevices, CanManageDeviceSecurity = canManageDeviceSecurity };

        var model = await SetupModel(permissions);

        Assert.Equal(expected, model.IsGuidedFirstInstall);
        Assert.Equal(expected, model.GuidedSetup is not null);
    }

    [Fact]
    public async Task OpeningTheSetupPage_CreatesNoSessionDeviceOrToken()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        // The session fake and the device fake fail the test on any create, regenerate or session call.
        var model = await SetupModel(Owner);

        Assert.True(model.IsGuidedFirstInstall);
        Assert.All(_devices.ListedTenants, tenant => Assert.Equal(_tenant, tenant));
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task AConnectedUser_StillContinuesToTheDeviceGuide_AndSetUpLaterStillSkipsIt()
    {
        var device = _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);
        _readiness.PrintingReady = true;
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        Assert.Equal($"{GuidedSetupCoordinator.DevicesUrl}/{device.Id}", (await Coordinator(Owner).ContinueAsync(_tenant, _user, Principal(), CancellationToken.None)).RedirectUrl);
        Assert.True((await SetupModel(Owner)).GuidedSetup!.IsReady);

        _readiness.PrintingReady = false;
        var later = await Coordinator(Owner).AdvanceAsync(_tenant, _user, Principal(), GuidedSetupSections.PrintBridge, CancellationToken.None);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, later.RedirectUrl);
        Assert.False((await SetupModel(Owner)).IsGuidedFirstInstall);
    }

    // Download ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDownload_IsTheExistingSameOriginPackageRoute_WithNoIdsTokensOrCodes()
    {
        var model = await SetupModel(Owner);
        var method = typeof(PrintBridgeController).GetMethod(nameof(PrintBridgeController.DownloadPackage))!;

        Assert.Equal("/print-bridge/download/package", model.PackageDownloadUrl);
        Assert.DoesNotMatch(new Regex(@"[?#]|tenant|user|token|code|[0-9a-f]{8}-", RegexOptions.IgnoreCase), model.PackageDownloadUrl);
        Assert.Equal("download/package", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(method.GetParameters());
        Assert.Equal(TenantPolicies.CanManageDeviceSecurity, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(TenantPolicies.CanManageDeviceSecurity,
            typeof(PrintBridgeController).GetMethod(nameof(PrintBridgeController.Setup))!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void TheDownload_ServesTheConfiguredPackageByName_AndCreatesNothing()
    {
        // A stand-in file created for this test only; no real binary is involved.
        var folder = Directory.CreateTempSubdirectory("wasla-print-bridge-download-");
        try
        {
            var package = Path.Combine(folder.FullName, "Wasla.PrintBridge-win-x64.zip");
            File.WriteAllText(package, "stand-in");

            var file = Assert.IsType<PhysicalFileResult>(Controller(Owner, packagePath: package).DownloadPackage());

            Assert.Equal(package, file.FileName);
            Assert.Equal("application/zip", file.ContentType);
            Assert.Equal("Wasla.PrintBridge-win-x64.zip", file.FileDownloadName);
            Assert.Empty(_devices.ListedTenants);
            Assert.IsType<NotFoundResult>(Controller(Owner, packagePath: Path.Combine(folder.FullName, "missing.zip")).DownloadPackage());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // Views and script -------------------------------------------------------------------------------

    [Fact]
    public void SetupPage_UsesTheFirstInstallInGuidedSetup_AndKeepsTheChoiceAndReconnectElsewhere()
    {
        var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");

        Assert.Matches(new Regex(@"@if \(Model\.IsGuidedFirstInstall\)\s*\{\s*@await Html\.PartialAsync\(""_PrintBridgeFirstInstall"", Model\)\s*\}\s*else\s*\{\s*<fieldset class=""mb-4"" id=""pbSetupModeGroup"">"), view);
        foreach (var ordinary in new[] { "id=\"pbSetupModeNew\"", "id=\"pbSetupModeReconnect\"", "id=\"pbReconnectDeviceSelect\"", "PrintBridge.Auto.ReconnectModeTitle" })
            Assert.Contains(ordinary, view, StringComparison.Ordinal);
        Assert.Contains("id=\"pbManualSetup\" hidden=\"@Model.IsGuidedFirstInstall\"", view, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"@if \(!Model\.IsGuidedFirstInstall\)\s*\{\s*<section class=""mb-4 wasla-print-bridge-package-card"" id=""pbPackageSection"">"), view);
        Assert.Contains("guidedFirstInstall = Model.IsGuidedFirstInstall,", view, StringComparison.Ordinal);
        Assert.Contains("@L[Model.IsGuidedFirstInstall ? \"PrintBridge.Manual.TokenIntroNewDevice\" : \"PrintBridge.Manual.TokenIntro\"]", view, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(view, @"PartialAsync\(""_PrintBridgeAutoStatus"""));
    }

    [Fact]
    public void FirstInstall_HasNoChoiceOrReconnect_AndStartsWithAnExplicitDownload()
    {
        var steps = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "_PrintBridgeFirstInstall.cshtml");
        var markup = Regex.Replace(steps, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        foreach (var forbidden in new[]
                 {
                     "pbSetupMode", "pbReconnect", "Reconnect", "type=\"radio\"", "<fieldset", "setup/session", "regenerate",
                     "SetupCode", "autoplay", "<iframe", "http-equiv", "location", "<script"
                 })
            Assert.DoesNotContain(forbidden, markup, StringComparison.OrdinalIgnoreCase);

        // Four ordered steps with fixed numbers; only the download step shows at first.
        Assert.Contains("<ol class=\"wasla-print-bridge-first-install\" id=\"pbFirstInstallSteps\">", markup, StringComparison.Ordinal);
        string[] ids = ["pbStepDownload", "pbStepPrepare", "pbStepConnect", "pbStepPrinter"];
        for (var i = 0; i < ids.Length; i++)
        {
            var item = Tag(markup, $"id=\"{ids[i]}\"");
            Assert.StartsWith($"<li value=\"{i + 1}\"", item, StringComparison.Ordinal);
            Assert.Equal(i > 0, item.Contains(" hidden", StringComparison.Ordinal));
        }
        Assert.Equal(4, Regex.Matches(markup, "<li value=").Count);

        // Step 1: a plain download link to the package route, only when the package exists.
        var download = Tag(markup, "id=\"pbDownloadBtn\"");
        Assert.Contains("href=\"@Model.PackageDownloadUrl\"", download, StringComparison.Ordinal);
        Assert.Contains("download=\"@Model.PackageFileName\"", download, StringComparison.Ordinal);
        Assert.Contains("class=\"btn btn-primary\"", download, StringComparison.Ordinal);
        Assert.Contains("@L[\"PrintBridge.FirstInstall.Download.Button\"]", markup, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"@if \(Model\.PackageAvailable\)\s*\{\s*<div class=""wasla-print-bridge-first-install__actions"">\s*<a class=""btn btn-primary"" id=""pbDownloadBtn"""), markup);
        // Missing package: an honest notice, no download control, and "already installed" still there.
        var missing = Section(markup, "else", "</li>");
        Assert.Contains("id=\"pbPackageMissingNotice\" role=\"note\"", missing, StringComparison.Ordinal);
        Assert.Contains("@L[\"PrintBridge.FirstInstall.Download.Missing\"]", missing, StringComparison.Ordinal);
        Assert.Contains("id=\"pbAlreadyInstalledBtn\"", missing, StringComparison.Ordinal);
        foreach (var fake in new[] { "pbDownloadBtn", "disabled", "PackageDownloadUrl", "<a " })
            Assert.DoesNotContain(fake, missing, StringComparison.Ordinal);

        // Step 2 confirms preparation; step 3 holds both connection choices and nothing earlier does.
        var prepare = Section(markup, "id=\"pbStepPrepare\"", "</li>\n\n");
        Assert.Contains("<button type=\"button\" class=\"btn btn-primary\" id=\"pbPreparedBtn\" aria-controls=\"pbStepConnect\">@L[\"PrintBridge.FirstInstall.Prepare.Done\"]</button>", prepare, StringComparison.Ordinal);
        var connect = Section(markup, "id=\"pbStepConnect\"", "id=\"pbStepPrinter\"");
        foreach (var control in new[] { "id=\"pbAutoOpenBtn\"", "id=\"pbChooseManualBtn\"", "_PrintBridgeAutoStatus" })
        {
            Assert.Contains(control, connect, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(markup, Regex.Escape(control)));
        }
        Assert.Contains("class=\"btn btn-primary\"", Tag(connect, "id=\"pbAutoOpenBtn\""), StringComparison.Ordinal);
        Assert.Contains("@L[\"PrintBridge.FirstInstall.Connect.Automatic.Button\"]", connect, StringComparison.Ordinal);
        Assert.Contains("@L[\"PrintBridge.Auto.RecommendedBadge\"]", connect, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"btn btn-outline-secondary\" id=\"pbChooseManualBtn\" aria-controls=\"pbManualSetup\">", connect, StringComparison.Ordinal);
        Assert.True(markup.IndexOf("id=\"pbDownloadBtn\"", StringComparison.Ordinal) < markup.IndexOf("id=\"pbPreparedBtn\"", StringComparison.Ordinal));
        Assert.True(markup.IndexOf("id=\"pbPreparedBtn\"", StringComparison.Ordinal) < markup.IndexOf("id=\"pbAutoOpenBtn\"", StringComparison.Ordinal));

        // Accessibility: focus targets for explicit moves, a polite status, real buttons.
        foreach (var title in new[] { "pbStepDownloadTitle", "pbStepPrepareTitle", "pbStepConnectTitle" })
            Assert.Contains("tabindex=\"-1\"", Tag(markup, $"id=\"{title}\""), StringComparison.Ordinal);
        Assert.Contains("id=\"pbDownloadStatus\" role=\"status\" aria-live=\"polite\"", markup, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"btn btn-link\" id=\"pbAlreadyInstalledBtn\" aria-controls=\"pbStepConnect\">", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"pbNotWindowsNotice\" role=\"note\"", markup, StringComparison.Ordinal);

        // No dead download link in the shared fallback during the first install.
        var status = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "_PrintBridgeAutoStatus.cshtml");
        Assert.Matches(new Regex(@"@if \(!Model\.IsGuidedFirstInstall \|\| Model\.PackageAvailable\)\s*\{\s*<a class=""btn btn-outline-secondary btn-sm"" href=""@Model\.PackageDownloadUrl"">"), status);

        // The guided panel has no Print Bridge top action any more; "set up later" and the next-section line stay.
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml");
        foreach (var removed in new[] { "SetUpNow", "#pbStepDownload", "pbSetupModeGroup" })
            Assert.DoesNotContain(removed, panel, StringComparison.Ordinal);
        Assert.Contains("@L[prefix + \".SetUpLater\"]", panel, StringComparison.Ordinal);
        Assert.Contains("@L[\"GuidedSetup.Panel.Next\"", panel, StringComparison.Ordinal);
        Assert.Contains("@L[\"GuidedSetup.PrintBridge.Install\", L[\"PrintBridge.FirstInstall.Connect.Automatic.Button\"].Value]", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocalPackage_StaysOutOfGit()
    {
        var ignore = File.ReadAllLines(Path.Combine(Root(), ".gitignore")).Select(line => line.Trim()).ToArray();

        // The release script's output folder and the Web app's default download folder are both ignored.
        Assert.Contains("artifacts/", ignore);
        Assert.Contains("src/Wasla.Web/wwwroot/downloads/wasla-print-bridge/*.zip", ignore);
        Assert.Equal("downloads/wasla-print-bridge", Wasla.Web.PrintBridge.PrintBridgePackagePaths.DefaultRelativeFolder);
        var script = Read("scripts", "release", "package-print-bridge.ps1");
        Assert.Contains("'artifacts\\print-bridge\\Wasla.PrintBridge-win-x64.zip'", script, StringComparison.Ordinal);
        Assert.Contains("'src\\Wasla.Web\\wwwroot\\downloads\\wasla-print-bridge'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupScript_CreatesTheSessionAndOpensTheApp_OnlyFromOpenAndConnect()
    {
        var script = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-setup.js");
        var automatic = Section(script, "function startAutomaticSetup()", "function stopSpinnerReset()");
        var firstInstall = Section(script, "// --- Guided first install", "function bindManualSectionLinks()");
        var onLoad = Section(script, "document.addEventListener(\"DOMContentLoaded\"", "})();");

        Assert.Single(Regex.Matches(script, @"fetch\(cfg\.sessionCreateUrl"));
        Assert.Contains("fetch(cfg.sessionCreateUrl", automatic, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, @"window\.location\.href = "));
        Assert.Contains("window.location.href = res.body.protocolUrl;", automatic, StringComparison.Ordinal);
        Assert.Contains("openBtn.addEventListener(\"click\", startAutomaticSetup)", script, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "fetch(", "postForm(", "startAutomaticSetup", "location.href", "manualDeviceCreateUrl", "regenerate" })
            Assert.DoesNotContain(forbidden, firstInstall, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "fetch(", "postForm(", "startAutomaticSetup()", "location" })
            Assert.DoesNotContain(forbidden, onLoad, StringComparison.Ordinal);
        Assert.Contains("if (!cfg.guidedFirstInstall) return;", firstInstall, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_FollowsDownloadExtractRunChooseConnectionThenTest()
    {
        var help = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml");
        var article = Section(help, "<article id=\"help-print-bridge\"", "</article>");
        var english = Load(".en-US");

        var steps = Regex.Matches(article, @"<li>@L\[""(Help\.PrintBridge\.Step\d)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "Help.PrintBridge.Step1", "Help.PrintBridge.Step2", "Help.PrintBridge.Step3", "Help.PrintBridge.Step4", "Help.PrintBridge.Step5", "Help.PrintBridge.Step6" }, steps);
        Assert.Contains("<li>@L[\"Help.PrintBridge.Step3\"]</li>", article, StringComparison.Ordinal);
        Assert.Contains("@L[\"Help.PrintBridge.Step4\", L[\"PrintBridge.FirstInstall.Connect.Automatic.Button\"].Value]", article, StringComparison.Ordinal);
        Assert.Contains("@L[\"Help.PrintBridge.Step6\", L[\"Help.PrintBridgeDevices.Title\"].Value]", article, StringComparison.Ordinal);

        Assert.Contains("download the Windows application package (ZIP)", english["Help.PrintBridge.Step1"], StringComparison.Ordinal);
        Assert.Contains("Extract the ZIP file", english["Help.PrintBridge.Step2"], StringComparison.Ordinal);
        Assert.Contains("Run Wasla.PrintBridge.exe once", english["Help.PrintBridge.Step3"], StringComparison.Ordinal);
        Assert.Contains(".NET 8 Desktop Runtime", english["Help.PrintBridge.Step3"], StringComparison.Ordinal);
        Assert.Contains("choose how to connect: \"{0}\"", english["Help.PrintBridge.Step4"], StringComparison.Ordinal);
        Assert.Contains("connect manually", english["Help.PrintBridge.Step4"], StringComparison.Ordinal);
        Assert.Contains("\"Print test receipt\"", english["Help.PrintBridge.Step5"], StringComparison.Ordinal);
        Assert.Contains("get to know your device", english["Help.PrintBridge.Step6"], StringComparison.Ordinal);
    }

    [Fact]
    public void SetupCopy_CallsItAWindowsApplicationPackage_NotAPortableInstaller()
    {
        var sources = new[]
        {
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "_PrintBridgeFirstInstall.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml")
        };
        var used = sources.SelectMany(s => Regex.Matches(s, "L\\[\"([A-Za-z0-9_.]+)\"").Select(m => m.Groups[1].Value)).Distinct().ToArray();

        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in used.Where(resources.ContainsKey))
                Assert.DoesNotMatch(new Regex("portable|taşınabilir|المحمولة|портатив", RegexOptions.IgnoreCase), resources[key]);
        }

        Assert.Contains("Windows application package", Load(".en-US")["PrintBridge.FirstInstall.Download.Title"], StringComparison.Ordinal);
        Assert.Equal("Wasla.PrintBridge-win-x64.zip", Load(".en-US")["PrintBridge.PackagePlaceholderFileName"]);
    }

    // Localization -----------------------------------------------------------------------------------

    [Fact]
    public void FirstInstallCopy_ExistsInAllFiveCultures_WithMatchingPlaceholders()
    {
        var english = Load(".en-US");
        var keys = english.Keys
            .Where(k => k.StartsWith("PrintBridge.FirstInstall.", StringComparison.Ordinal) || Regex.IsMatch(k, @"^Help\.PrintBridge\.Step\d$"))
            .Append("PrintBridge.Manual.TokenIntroNewDevice")
            .ToArray();
        Assert.Equal(22 + 6 + 1, keys.Length);

        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{key} is missing in '{culture}'.");
                Assert.Equal(Placeholders(english[key]), Placeholders(resources[key]));
                Assert.DoesNotMatch(new Regex("setup code|one-time code|kurulum kod", RegexOptions.IgnoreCase), resources[key]);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".tr-TR")]
    [InlineData(".en-US")]
    [InlineData(".ar-SA")]
    [InlineData(".ru-RU")]
    public void PrinterSteps_NameTheDesktopAppsRealControls(string culture)
    {
        var copy = Load(culture);
        var desktop = LoadResx(Path.Combine(Root(), "src", "Wasla.PrintBridge", "Resources", $"PrintBridgeResources{culture}.resx"));

        foreach (var key in new[] { "PrintBridge.FirstInstall.Printer.Body", "Help.PrintBridge.Step5" })
        foreach (var label in new[] { desktop["Tab.Settings"], desktop["Button.SavePrinter"], desktop["Button.TestPrinter"] })
            Assert.Contains(label, copy[key], StringComparison.Ordinal);
        Assert.Contains(desktop["Settings.Section.Printer"], copy["PrintBridge.FirstInstall.Printer.Body"], StringComparison.Ordinal);
        Assert.Contains("Wasla.PrintBridge.exe", copy["PrintBridge.FirstInstall.Prepare.Run"], StringComparison.Ordinal);
        Assert.Contains(".NET 8 Desktop Runtime", copy["PrintBridge.FirstInstall.Prepare.Runtime"], StringComparison.Ordinal);
        Assert.Contains("ZIP", copy["PrintBridge.FirstInstall.Download.Requirement"], StringComparison.Ordinal);
        Assert.Contains("Windows 10", copy["PrintBridge.FirstInstall.Download.Requirement"], StringComparison.Ordinal);
    }

    // Helpers ----------------------------------------------------------------------------------------

    private ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _user.ToString())], "Tenant"));

    private GuidedSetupCoordinator Coordinator(TenantNavigationPermissions permissions) =>
        new(_state, new FixedNavigation(permissions), _readiness, new FakeDemos(), _devices, new CapturingLogger());

    private async Task<PrintBridgeSetupViewModel> SetupModel(TenantNavigationPermissions permissions) =>
        Assert.IsType<PrintBridgeSetupViewModel>(Assert.IsType<ViewResult>(await Controller(permissions).Setup(CancellationToken.None)).Model);

    private PrintBridgeController Controller(TenantNavigationPermissions permissions, string? packagePath = null)
    {
        var settings = new Dictionary<string, string?> { ["OrderHub:CustomerWebBaseUrl"] = "https://tenant.wasla.local/" };
        if (packagePath is not null)
            settings["OrderHub:PrintBridgeDownload:PackagePath"] = packagePath;

        var httpContext = new DefaultHttpContext { User = Principal() };
        return new PrintBridgeController(
            new FixedTenant(_tenant),
            _devices,
            new NoSetupSessions(),
            null!,
            new TestHostEnvironment("Production"),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new KeyLocalizer(),
            Coordinator(permissions),
            new AllowAll())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
            Url = new NoRouteUrlHelper()
        };
    }

    private static string[] Placeholders(string value) =>
        PlaceholderPattern().Matches(value).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The text from <paramref name="start"/> up to the next <paramref name="end"/>.</summary>
    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
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

    private static Dictionary<string, string> LoadResx(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value.Trim() ?? string.Empty, StringComparer.Ordinal);

    // Normalized so the contract does not depend on the checkout's line endings (CRLF on Windows with autocrlf).
    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([Root(), .. segments])).ReplaceLineEndings("\n");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Opening the page or downloading must never touch setup sessions.</summary>
    private sealed class NoSetupSessions : IPrintBridgeSetupSessionService
    {
        public Task<PrintBridgeSetupSessionCreated> CreateSessionAsync(Guid tenantId, PrintBridgeSetupMode setupMode, Guid? deviceId, string serverUrl, string? defaultDeviceName, bool confirmReplaceActiveToken, CancellationToken ct) => throw new InvalidOperationException("No setup session may be created here.");
        public Task<PrintBridgeSetupExchangeResult?> ExchangeAsync(string rawCode, PrintBridgeSetupClientInfo clientInfo, CancellationToken ct) => throw new InvalidOperationException();
        public Task<bool> CompleteAsync(Guid sessionId, string completionCredential, bool connectionVerified, CancellationToken ct) => throw new InvalidOperationException();
        public Task<PrintBridgeSetupStatusDto?> GetStatusAsync(Guid tenantId, Guid sessionId, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class AllowAll : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

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
