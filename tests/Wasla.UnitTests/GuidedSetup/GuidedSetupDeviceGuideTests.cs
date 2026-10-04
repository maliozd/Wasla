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
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;
using Wasla.UnitTests.DevelopmentTools;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.GuidedSetup;
using Wasla.Web.Models.PrintBridge;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.GuidedSetup;

/// <summary>
/// The Print Bridge section's device guide: a connected Print Bridge continues on the device pages with an
/// informational guide, derived from the saved section plus the checklist's readiness fact (no new state). Only the
/// guide's explicit Continue moves on; "set it up later" skips it; finished journeys never show it.
/// </summary>
public sealed partial class GuidedSetupDeviceGuideTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);
    private const string DevicesUrl = GuidedSetupCoordinator.DevicesUrl;
    private const string SetupUrl = "/print-bridge/setup";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly ClaimsPrincipal _principal;
    private readonly FakeGuidedSetup _state = new();
    private readonly FakeSetupStatus _readiness = new();
    private readonly FakePrintBridgeDevices _devices = new();
    private readonly CapturingLogger _logger = new();

    public GuidedSetupDeviceGuideTests()
    {
        _principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _user.ToString())], "Tenant"));
    }

    // Routing ------------------------------------------------------------------------------------

    [Fact]
    public async Task NotConnected_RoutesToTheSetupPage_AndShowsNoGuide()
    {
        AtPrintBridge(connected: false);
        var coordinator = Coordinator();

        Assert.Equal(SetupUrl, (await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None)).RedirectUrl);
        Assert.Equal(SetupUrl, await coordinator.GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Null(await coordinator.GetDeviceGuideAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Empty(_devices.ListedTenants);
    }

    [Fact]
    public async Task Connected_StartResumeAndTheGuideLink_AllOpenTheConnectedDevicesPage()
    {
        var device = AtPrintBridge(connected: true);
        var coordinator = Coordinator();
        var expected = $"{DevicesUrl}/{device.Id}";

        Assert.Equal(expected, (await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None)).RedirectUrl);
        Assert.Equal(expected, (await coordinator.StartAsync(_tenant, _user, _principal, CancellationToken.None)).RedirectUrl);
        Assert.Equal(expected, await coordinator.GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
        // Still the Print Bridge section; nothing moved on.
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
    }

    [Fact]
    public async Task LeavingPlatformsWithPrintBridgeAlreadyConnected_LandsOnTheDeviceGuide()
    {
        var device = _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);
        _readiness.PrintingReady = true;
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);

        var result = await Coordinator().AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);

        Assert.Equal($"{DevicesUrl}/{device.Id}", result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);
    }

    [Fact]
    public async Task SetUpLater_SkipsTheGuide_AndGoesStraightToOrderTraining()
    {
        AtPrintBridge(connected: false);

        var result = await Coordinator().AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Empty(_devices.ListedTenants);
    }

    [Fact]
    public async Task TheGuidesExplicitContinue_IsWhatMovesOnToOrderTraining()
    {
        AtPrintBridge(connected: true);

        var result = await Coordinator().ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
        Assert.Null(result.ErrorMessageKey);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(1, _state.Writes);
        Assert.Null(await Coordinator().GetDeviceGuideAsync(_tenant, _user, _principal, CancellationToken.None));
    }

    [Fact]
    public async Task ViewingRefreshingAndResuming_NeverMoveOn_UntilContinue()
    {
        var device = AtPrintBridge(connected: true);
        var coordinator = Coordinator();

        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await coordinator.GetDeviceGuideAsync(_tenant, _user, _principal, CancellationToken.None));
            Assert.Equal($"{DevicesUrl}/{device.Id}", await coordinator.GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
            Assert.Equal($"{DevicesUrl}/{device.Id}", (await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None)).RedirectUrl);
            Assert.True((await coordinator.GetSectionStatusAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None)).IsReady);
        }

        Assert.Equal(0, _state.Writes);
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);

        await coordinator.ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, (await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None)).RedirectUrl);
    }

    [Fact]
    public async Task ADisconnectBeforeContinue_ReturnsToSetup_WithoutMovingOn()
    {
        AtPrintBridge(connected: true);
        var coordinator = Coordinator();
        _readiness.PrintingReady = false;

        var result = await coordinator.ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(SetupUrl, result.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.NotReadyYetMessageKey, result.ErrorMessageKey);
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);
    }

    [Fact]
    public async Task TheGuideFlow_OnlyReadsTheDeviceList_AndNeverIssuesOrRemovesAnything()
    {
        // The fake fails the test on any token, device or activation change.
        AtPrintBridge(connected: true);
        var coordinator = Coordinator();

        await coordinator.GetDeviceGuideAsync(_tenant, _user, _principal, CancellationToken.None);
        await coordinator.GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None);
        await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None);
        await coordinator.ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.All(_devices.ListedTenants, tenant => Assert.Equal(_tenant, tenant));
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.Skipped)]
    [InlineData(GuidedSetupStatus.Completed)]
    [InlineData(GuidedSetupStatus.NotStarted)]
    public async Task FinishedOrUnstartedJourneys_ShowNoGuide_AndNeverRouteToIt(GuidedSetupStatus status)
    {
        _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);
        _readiness.PrintingReady = true;
        _state.Set(_user, status, status == GuidedSetupStatus.NotStarted ? null : GuidedSetupSections.PrintBridge);
        var coordinator = Coordinator();

        Assert.Null(await coordinator.GetDeviceGuideAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Equal("/dashboard", await coordinator.GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Equal("/dashboard", (await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None)).RedirectUrl);
        Assert.Empty(_devices.ListedTenants);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UsersMissingEitherPrintBridgePolicy_GetNoGuide(bool canManageDevices, bool canManageDeviceSecurity)
    {
        AtPrintBridge(connected: true);
        var permissions = Owner with { CanManagePrintBridgeDevices = canManageDevices, CanManageDeviceSecurity = canManageDeviceSecurity };

        var coordinator = Coordinator(permissions);

        Assert.Null(await coordinator.GetDeviceGuideAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.DoesNotContain(DevicesUrl, await coordinator.GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None), StringComparison.Ordinal);
    }

    // Choosing the page ---------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheCurrentTenantsDevices_AreConsidered()
    {
        var other = Guid.NewGuid();
        _devices.Add(other, "Other tenant", lastSeenAtUtc: DateTime.UtcNow);
        _readiness.PrintingReady = true;
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        // This tenant has no connected device of its own: another tenant's is never picked.
        Assert.Equal(DevicesUrl, await Coordinator().GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));

        var mine = _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);
        Assert.Equal($"{DevicesUrl}/{mine.Id}", await Coordinator().GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.All(_devices.ListedTenants, tenant => Assert.Equal(_tenant, tenant));
    }

    [Fact]
    public async Task ASpecificDevice_IsChosenOnlyWhenExactlyOneIsConnectedNow()
    {
        _readiness.PrintingReady = true;
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        var connected = _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: DateTime.UtcNow);
        _devices.Add(_tenant, "Bar, seen an hour ago", lastSeenAtUtc: DateTime.UtcNow.AddHours(-1));
        _devices.Add(_tenant, "Office, never connected");
        _devices.Add(_tenant, "Disabled but recent", isActive: false, lastSeenAtUtc: DateTime.UtcNow);

        Assert.Equal($"{DevicesUrl}/{connected.Id}", await Coordinator().GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));

        // A second connected device makes the choice ambiguous: nothing is guessed.
        _devices.Add(_tenant, "Second kitchen", lastSeenAtUtc: DateTime.UtcNow);
        Assert.Equal(DevicesUrl, await Coordinator().GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
    }

    [Fact]
    public async Task WhenNoDeviceCanBeChosenSafely_TheGuideOpensOnTheDeviceList()
    {
        _readiness.PrintingReady = true;
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        // Readiness and the device list disagree for a moment (a heartbeat just expired): the list, never a guess.
        Assert.Equal(DevicesUrl, await Coordinator().GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));

        // The device list cannot be read: the list page, and a logged warning without details.
        _devices.FailReads = true;
        Assert.Equal(DevicesUrl, await Coordinator().GetCurrentSectionUrlAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    // Web --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGuideLink_IsAReadOnlyGet_ThatRedirectsToTheServersChoice()
    {
        var device = AtPrintBridge(connected: true);
        var controller = new GuidedSetupController(new FixedTenant(_tenant), Coordinator(), new KeyLocalizer());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = _principal } };

        var result = Assert.IsType<LocalRedirectResult>(await controller.PrintBridgeDevice(CancellationToken.None));

        Assert.Equal($"{DevicesUrl}/{device.Id}", result.Url);
        Assert.Equal(0, _state.Writes);
        var method = typeof(GuidedSetupController).GetMethod(nameof(GuidedSetupController.PrintBridgeDevice))!;
        Assert.Equal("print-bridge/device", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Null(method.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal(new[] { typeof(CancellationToken) }, method.GetParameters().Select(p => p.ParameterType));
        Assert.Equal("/guided-setup/print-bridge/device", GuidedSetupCoordinator.DeviceGuideEntryUrl);
    }

    [Fact]
    public async Task DevicePages_ShowTheGuide_OnlyWhileItApplies()
    {
        var device = AtPrintBridge(connected: true);

        var list = Assert.IsType<PrintBridgePageViewModel>(Assert.IsType<ViewResult>(await DeviceController().Devices(CancellationToken.None)).Model);
        var details = Assert.IsType<PrintBridgeDeviceDetailsViewModel>(Assert.IsType<ViewResult>(await DeviceController().DeviceDetails(device.Id, CancellationToken.None)).Model);

        Assert.NotNull(list.GuidedDeviceGuide);
        Assert.False(list.GuidedDeviceGuide!.ForOneDevice);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, list.GuidedDeviceGuide.NextSectionKey);
        Assert.True(details.GuidedDeviceGuide!.ForOneDevice);

        // After Continue the same pages are ordinary device pages again.
        await Coordinator().ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        list = Assert.IsType<PrintBridgePageViewModel>(Assert.IsType<ViewResult>(await DeviceController().Devices(CancellationToken.None)).Model);
        Assert.Null(list.GuidedDeviceGuide);
    }

    [Fact]
    public async Task AnotherTenantsDeviceId_IsNotFound_AndShowsNoGuide()
    {
        AtPrintBridge(connected: true);
        var foreign = _devices.Add(Guid.NewGuid(), "Other tenant", lastSeenAtUtc: DateTime.UtcNow);

        Assert.IsType<NotFoundResult>(await DeviceController().DeviceDetails(foreign.Id, CancellationToken.None));
    }

    [Fact]
    public void DevicePages_KeepTheirOwnPolicy()
    {
        foreach (var action in new[] { nameof(PrintBridgeController.Devices), nameof(PrintBridgeController.DeviceDetails) })
        {
            var method = typeof(PrintBridgeController).GetMethod(action)!;
            Assert.Equal(TenantPolicies.CanManagePrintBridgeDevices, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        }
    }

    // Views ------------------------------------------------------------------------------------------

    [Fact]
    public void ConnectedPrintBridgePanel_LinksToTheGuide_InsteadOfContinuing()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml");
        var ready = Block(panel, "<div class=\"wasla-guided-setup-panel__state\" data-guided-setup-when=\"ready\" hidden=\"@(!ready)\">", "\n        </div>");

        Assert.Matches(new Regex(@"else\s*\{[^{}]*<a class=""btn btn-primary"" href=""@GuidedSetupCoordinator\.DeviceGuideEntryUrl"" data-guided-setup-continue>@L\[""GuidedSetup\.PrintBridge\.MeetDevice""\]</a>\s*\}", RegexOptions.Singleline), ready);
        // The section Continue form stays only for the platform section.
        Assert.Matches(new Regex(@"@if \(isPlatform\)\s*\{[^{}]*<form method=""post"" action=""/guided-setup/section-continue""", RegexOptions.Singleline), ready);
    }

    [Fact]
    public void TheGuide_IsInformational_WithTheSectionContinueAsItsOnlyAction()
    {
        var guide = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "_GuidedDeviceGuide.cshtml");
        var topics = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_PrintBridgeDeviceTopics.cshtml");
        var withoutComments = Regex.Replace(guide + topics, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        Assert.Single(Regex.Matches(withoutComments, "<form"));
        Assert.Contains("<form method=\"post\" action=\"/guided-setup/section-continue\" data-guided-setup-form>", guide, StringComparison.Ordinal);
        Assert.Contains("<input type=\"hidden\" name=\"section\" value=\"@GuidedSetupSections.PrintBridge\" />", guide, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\" class=\"btn btn-primary\">@L[GuidedSetupCopy.ContinueToKey(Model.NextSectionKey)]</button>", guide, StringComparison.Ordinal);
        Assert.Equal("GuidedSetup.Panel.ContinueToOrderTraining", GuidedSetupCopy.ContinueToKey(GuidedSetupSections.LiveScreenDemo));
        // No device action, link, script or automatic behaviour of its own.
        foreach (var forbidden in new[]
                 {
                     "regenerate-token", "/remove", "set-active", "asp-action", "formaction", "<a ", "<button type=\"button\"",
                     "onclick", "fetch(", "data-bs-toggle", "modal", "setTimeout"
                 })
            Assert.DoesNotContain(forbidden, withoutComments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<script src=\"~/js/wasla-guided-setup.js\"", guide, StringComparison.Ordinal);
        // Headings and lists with real semantics.
        Assert.Contains("<h2 id=\"waslaGuidedDeviceGuideTitle\"", guide, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"waslaGuidedDeviceGuideTitle\"", guide, StringComparison.Ordinal);
        Assert.Contains("<dl class=\"wasla-print-bridge-device-topics\">", topics, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(topics, "<dt>").Count);
        Assert.Equal(2, Regex.Matches(topics, "<ul>").Count);
    }

    [Fact]
    public void DevicePages_RenderTheGuideOnlyFromTheServerDecision_AndMountNoLegacyTour()
    {
        foreach (var page in new[] { "Devices.cshtml", "DeviceDetails.cshtml" })
        {
            var view = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", page);
            Assert.Matches(new Regex(@"@if \(Model\.GuidedDeviceGuide is not null\)\s*\{\s*@await Html\.PartialAsync\(""_GuidedDeviceGuide"", Model\.GuidedDeviceGuide\)\s*\}"), view);
            AssertNoLegacyTour(view, page);
        }

        AssertNoLegacyTour(Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "_GuidedDeviceGuide.cshtml"), "_GuidedDeviceGuide");
        AssertNoLegacyTour(Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_PrintBridgeDeviceTopics.cshtml"), "_PrintBridgeDeviceTopics");
    }

    [Fact]
    public void Help_KeepsExplainingTokenRegenerationAndDeviceRemoval()
    {
        var help = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml");
        var article = Block(help, "<article id=\"help-print-bridge-devices\"", "</article>");
        var english = Load(".en-US");

        Assert.Contains("(\"help-print-bridge-devices\", \"Help.PrintBridgeDevices.Title\")", help, StringComparison.Ordinal);
        Assert.Contains("<partial name=\"_PrintBridgeDeviceTopics\" />", article, StringComparison.Ordinal);
        Assert.Contains("Model.CanOpenPrintBridgeDevices", article, StringComparison.Ordinal);
        // The same explanation as the guide, permanently: what each action does and when to use it.
        Assert.Contains("stops working immediately", english["Help.PrintBridgeDevices.Regenerate.OldStops"], StringComparison.Ordinal);
        Assert.Contains("shown only once", english["Help.PrintBridgeDevices.Regenerate.ShownOnce"], StringComparison.Ordinal);
        Assert.Contains("Settings", english["Help.PrintBridgeDevices.Regenerate.EnterAgain"], StringComparison.Ordinal);
        Assert.Contains("no longer receives print jobs", english["Help.PrintBridgeDevices.Remove.Jobs"], StringComparison.Ordinal);
        Assert.Contains("retired, lost or replaced", english["Help.PrintBridgeDevices.Remove.When"], StringComparison.Ordinal);
    }

    // Localization -----------------------------------------------------------------------------------

    [Fact]
    public void GuideCopy_ExistsInAllFiveCultures_WithMatchingPlaceholders()
    {
        var english = Load(".en-US");
        var keys = english.Keys
            .Where(k => k.StartsWith("Help.PrintBridgeDevices.", StringComparison.Ordinal) || k.StartsWith("GuidedSetup.DeviceGuide.", StringComparison.Ordinal))
            .Append("GuidedSetup.PrintBridge.MeetDevice")
            .ToArray();
        Assert.Equal(21, keys.Length);

        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{key} is missing in '{culture}'.");
                Assert.Equal(PlaceholderPattern().Matches(english[key]).Select(m => m.Value), PlaceholderPattern().Matches(resources[key]).Select(m => m.Value));
                Assert.DoesNotMatch(new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-|[A-Za-z0-9_-]{32,}"), resources[key]);
            }
        }

        Assert.Equal("Cihazını tanı", Load(".tr-TR")["GuidedSetup.PrintBridge.MeetDevice"]);
        Assert.Equal("Cihazını tanı", Load("")["GuidedSetup.PrintBridge.MeetDevice"]);
    }

    [Theory]
    [InlineData("", ".tr-TR")]
    [InlineData(".tr-TR", ".tr-TR")]
    [InlineData(".en-US", ".en-US")]
    [InlineData(".ar-SA", ".ar-SA")]
    [InlineData(".ru-RU", ".ru-RU")]
    public void GuideCopy_NamesTheRealButtonsAndTheDesktopSettings(string culture, string uiCulture)
    {
        var copy = Load(culture);
        var ui = Load(uiCulture);
        var desktop = LoadResx(Path.Combine(Root(), "src", "Wasla.PrintBridge", "Resources", $"PrintBridgeResources{culture}.resx"));

        Assert.Equal(ui["PrintBridge.RegenerateToken"], copy["Help.PrintBridgeDevices.Regenerate.Title"]);
        Assert.Equal(ui["PrintBridge.RemoveDevice"], copy["Help.PrintBridgeDevices.Remove.Title"]);
        Assert.Equal(desktop["Settings.AgentToken"], copy["Help.PrintBridgeDevices.Token.Title"]);
        Assert.Contains(desktop["Tab.Settings"], copy["Help.PrintBridgeDevices.Regenerate.EnterAgain"], StringComparison.Ordinal);
        Assert.Contains(ui["PrintBridge.StatusConnected"], copy["Help.PrintBridgeDevices.Status.Body"], StringComparison.Ordinal);
    }

    // Helpers ----------------------------------------------------------------------------------------

    private PrintBridgeDeviceSummaryDto AtPrintBridge(bool connected)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = connected;
        return _devices.Add(_tenant, "Kitchen", lastSeenAtUtc: connected ? DateTime.UtcNow : null);
    }

    private GuidedSetupCoordinator Coordinator(TenantNavigationPermissions? permissions = null) =>
        new(_state, new FixedNavigation(permissions ?? Owner), _readiness, new FakeDemos(), _devices, _logger);

    private PrintBridgeController DeviceController()
    {
        var httpContext = new DefaultHttpContext { User = _principal };
        return new PrintBridgeController(
            new FixedTenant(_tenant),
            _devices,
            null!,
            new NoPrintJobs(),
            new TestHostEnvironment("Development"),
            new ConfigurationBuilder().Build(),
            new KeyLocalizer(),
            Coordinator(),
            null!)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
            Url = new NoRouteUrlHelper()
        };
    }

    /// <summary>The text from <paramref name="start"/> up to the next <paramref name="end"/>.</summary>
    private static string Block(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
        return source[from..to];
    }

    private static void AssertNoLegacyTour(string view, string name)
    {
        foreach (var forbidden in new[]
                 {
                     "InvokeAsync(\"ProductTour\"", "<vc:product-tour", "ProductTour", "suppressAutoStart", "data-wasla-tour",
                     "wasla-tour.js", "WaslaTour.", "Tour.Replay", "waslaTourSkipModal", "coachmark"
                 })
            Assert.False(view.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{name} contains the legacy tour marker '{forbidden}'.");
    }

    private static Dictionary<string, string> Load(string culture) =>
        LoadResx(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"));

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

    private sealed class NoPrintJobs : IPrintJobHistoryService
    {
        public Task<IReadOnlyList<PrintJobHistoryItemDto>> GetRecentReceiptJobsAsync(Guid customerId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrintJobHistoryItemDto>>([]);

        public Task<ReprintReceiptResult> CreateReprintAsync(Guid customerId, Guid printJobId, string? tenantDisplayName, CancellationToken ct) =>
            throw new InvalidOperationException("The device guide must not reprint.");
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
