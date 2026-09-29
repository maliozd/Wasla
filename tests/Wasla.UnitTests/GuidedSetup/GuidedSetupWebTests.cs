using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.GuidedSetup;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.Help;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.GuidedSetup;

public sealed partial class GuidedSetupWebTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    // Controller --------------------------------------------------------------------------

    [Fact]
    public async Task Start_RedirectsToTheFirstSection_ForTheSignedInUser()
    {
        var state = new FakeGuidedSetup();
        var controller = Controller(state);

        var result = await controller.Start(CancellationToken.None);

        Assert.Equal("/platform-connections", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal(GuidedSetupStatus.InProgress, state.Get(_userId).Status);
    }

    [Fact]
    public async Task Skip_ShowsTheLocalizedConfirmationToast_AndRecordsSkipped()
    {
        var state = new FakeGuidedSetup();
        var controller = Controller(state);

        var result = await controller.Skip(CancellationToken.None);

        Assert.Equal("/dashboard", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal("GuidedSetup.Closed", controller.TempData["Success"]);
        Assert.Equal(GuidedSetupController.ClosedToastDurationMs, controller.TempData["SuccessDurationMs"]);
        Assert.InRange(GuidedSetupController.ClosedToastDurationMs, 4000, 5000);
        Assert.Equal(GuidedSetupStatus.Skipped, state.Get(_userId).Status);
    }

    [Fact]
    public async Task OnlyTheClosedToast_AsksForTheLongerDuration()
    {
        var start = Controller(new FakeGuidedSetup());
        await start.Start(CancellationToken.None);
        Assert.False(start.TempData.ContainsKey("SuccessDurationMs"));

        var inProgress = new FakeGuidedSetup();
        inProgress.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        var end = Controller(inProgress);
        await end.End(confirmed: false, CancellationToken.None);
        Assert.False(end.TempData.ContainsKey("SuccessDurationMs"));

        await end.End(confirmed: true, CancellationToken.None);
        Assert.Equal("GuidedSetup.Closed", end.TempData["Success"]);
        Assert.Equal(GuidedSetupController.ClosedToastDurationMs, end.TempData["SuccessDurationMs"]);
    }

    [Fact]
    public void Notifications_PassADurationOnlyWhenOneIsSet_AndKeepTheGlobalDefaults()
    {
        var partial = Read("src", "Wasla.Web", "Views", "Shared", "_Notifications.cshtml");
        var toast = Read("src", "Wasla.Web", "wwwroot", "js", "wasla-toast.js");

        Assert.Contains("TempData[\"SuccessDurationMs\"] is int ms && ms > 0 ? ms : null", partial, StringComparison.Ordinal);
        Assert.Contains("window.WaslaToast.success(data.success, data.successDurationMs ? { durationMs: data.successDurationMs } : undefined);", partial, StringComparison.Ordinal);
        Assert.Contains("if (data.error)   window.WaslaToast.error(data.error);", partial, StringComparison.Ordinal);
        // The shared toast keeps its defaults and its polite status announcement.
        Assert.Contains("const DEFAULT_DURATION = { success: 1400, info: 1800, warning: 2200, error: 3000 };", toast, StringComparison.Ordinal);
        Assert.Contains("toast.setAttribute(\"role\", \"status\");", toast, StringComparison.Ordinal);
        Assert.Contains("toast.setAttribute(\"aria-live\", \"polite\");", toast, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_OnlyActsOnTheConfirmationField()
    {
        var state = new FakeGuidedSetup();
        state.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        var controller = Controller(state);

        await controller.End(confirmed: false, CancellationToken.None);
        Assert.Equal(GuidedSetupStatus.InProgress, state.Get(_userId).Status);

        await controller.End(confirmed: true, CancellationToken.None);
        Assert.Equal(GuidedSetupStatus.Skipped, state.Get(_userId).Status);
    }

    [Fact]
    public async Task Commands_UseOnlyTheAuthenticatedUser()
    {
        var state = new FakeGuidedSetup();
        var controller = Controller(state, userIdClaim: "not-a-guid");

        var result = await controller.Skip(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, state.Writes);
        // No action takes a user id from the request.
        foreach (var method in CommandMethods())
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name!.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Commands_ArePostsWithAntiforgery_ForAnyTenantRole()
    {
        var authorize = typeof(GuidedSetupController).GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Equal(TenantPolicies.CanViewOrders, authorize.Policy);
        Assert.Equal(AuthSchemes.Tenant, authorize.AuthenticationSchemes);

        var methods = CommandMethods().ToArray();
        Assert.Equal(5, methods.Length);
        Assert.All(methods, method =>
        {
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.Null(method.GetCustomAttribute<HttpGetAttribute>());
        });
    }

    [Fact]
    public async Task Help_ShowsActionLinksOnlyForPermittedPages_AndUsesTheCanonicalWindow()
    {
        var kitchen = new TenantNavigationPermissions(false, true, true, true, false, false, false, false, false, false);
        var controller = new HelpController(new FixedNavigation(kitchen))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var model = Assert.IsType<HelpPageViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);

        Assert.False(model.CanOpenPlatformConnections);
        Assert.False(model.CanOpenPrintBridgeSetup);
        Assert.False(model.CanOpenPrintBridgeDevices);
        Assert.False(model.CanOpenReceiptPrinterSettings);
        Assert.True(model.CanOpenLiveScreen);
        Assert.True(model.CanOpenOrders);
        Assert.Equal((int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes, model.DeliveredWindowMinutes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Dashboard_RendersTheCard_OrStillRendersWhenStateCannotBeRead(bool readFails, bool expectCard)
    {
        var state = new FakeGuidedSetup { FailReads = readFails };
        var logger = new CapturingLogger();
        var coordinator = new GuidedSetupCoordinator(state, new FixedNavigation(Owner), new FakeSetupStatus(), new FakeDemos(), logger);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Tenant"))
        };
        var controller = new DashboardController(
            new FixedTenant(_tenantId),
            new EmptyDashboard(),
            new FakeSetupStatus(),
            new DenyAll(),
            coordinator,
            new KeyLocalizer(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DashboardController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var view = Assert.IsType<ViewResult>(await controller.Index(CancellationToken.None));
        var model = Assert.IsType<Wasla.Web.Models.Dashboard.DashboardViewModel>(view.Model);

        Assert.Equal(expectCard, model.GuidedSetup is not null);
        Assert.Equal(readFails, logger.Entries.Any(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error));
    }

    // Views -------------------------------------------------------------------------------

    [Fact]
    public void DashboardCard_IsAnOptionalCard_WithPostFormsAndAnAccessibleEndDialog()
    {
        var card = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_GuidedSetupCard.cshtml");

        foreach (var action in new[] { "/guided-setup/start", "/guided-setup/skip", "/guided-setup/continue", "/guided-setup/end" })
            Assert.Contains($"method=\"post\" action=\"{action}\"", card, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(card, "@Html.AntiForgeryToken\\(\\)").Count);
        Assert.Contains("name=\"confirmed\" value=\"true\"", card, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"waslaGuidedSetupEndTitle\"", card, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"waslaGuidedSetupEndBody\"", card, StringComparison.Ordinal);
        Assert.Contains("data-bs-toggle=\"modal\" data-bs-target=\"#waslaGuidedSetupEndDialog\"", card, StringComparison.Ordinal);
        Assert.Contains("data-guided-setup-cancel", card, StringComparison.Ordinal);
        // The first-use Skip has no second confirmation; only ending a started journey does.
        Assert.DoesNotContain("confirm(", card, StringComparison.Ordinal);
        // The order-training handoff never starts or finishes a demo from the page.
        foreach (var forbidden in new[] { "/orders/demo", "wasla-demo", "L[\"Demo.", "ProductTour" })
            Assert.DoesNotContain(forbidden, card, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_ShowsTheCard_AndMountsNoLegacyTour()
    {
        var dashboard = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "Index.cshtml");
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_TenantSetupPanel.cshtml");

        Assert.Contains("_GuidedSetupCard", dashboard, StringComparison.Ordinal);
        AssertNoLegacyTour(dashboard, "Dashboard");
        // The operational checklist remains; only its welcome banner yields to the guided card.
        Assert.Contains("_TenantSetupPanel", dashboard, StringComparison.Ordinal);
        // Hiding the welcome must never fall through to the "ready" banner for a tenant that is not ready.
        Assert.Matches(new Regex(@"@if \(!Model\.IsReady && hideWelcome\)\s*\{[^{}]*\}\s*else if \(!Model\.IsReady\)\s*\{\s*<div class=""wasla-setup-welcome""", RegexOptions.Singleline), panel);
        Assert.True(dashboard.IndexOf("</header>", StringComparison.Ordinal) < dashboard.IndexOf("_GuidedSetupCard", StringComparison.Ordinal),
            "the card follows the page heading");
    }

    [Fact]
    public void SectionPages_ShowTheGuidedPanel_AndMountNoLegacyTour()
    {
        var platforms = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PlatformConnections", "Index.cshtml");
        var printBridge = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "Setup.cshtml");
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml");

        // The panel is the only guidance, and only while it is the user's current section; otherwise nothing.
        Assert.Matches(new Regex(@"@if \(Model\.GuidedSetup is not null\)\s*\{\s*@await Html\.PartialAsync\(""_GuidedSetupSectionPanel"", Model\.GuidedSetup\)\s*\}\s*<section", RegexOptions.Singleline), platforms);
        AssertNoLegacyTour(platforms, "Platform Connections");
        AssertNoLegacyTour(printBridge, "Print Bridge setup");
        Assert.Contains("_GuidedSetupSectionPanel", printBridge, StringComparison.Ordinal);
        Assert.Contains("action=\"/guided-setup/advance\"", panel, StringComparison.Ordinal);
        Assert.Contains("name=\"section\" value=\"@Model.SectionKey\"", panel, StringComparison.Ordinal);
        Assert.Contains("@Html.AntiForgeryToken()", panel, StringComparison.Ordinal);
        // Real work stays on the existing pages: no credential fields, tokens or device creation here.
        var withoutAntiforgery = panel.Replace("@Html.AntiForgeryToken()", string.Empty, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "ApiKey", "ApiSecret", "token", "setup/session", "regenerate", "type=\"password\"" })
            Assert.DoesNotContain(forbidden, withoutAntiforgery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveScreen_MountsNeitherLegacyTour_NorTheLegacyTrainingLauncher()
    {
        var live = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");

        AssertNoLegacyTour(live, "Live Screen");
        foreach (var forbidden in new[]
                 {
                     "GuidedDemo", "LiveScreenIntro", "wasla-demo-launch", "action=\"/orders/demo\"", "Demo.LaunchTitle",
                     "Demo.LaunchAction", "waslaTourSkipModal", "data-tour-skip-confirm", "wasla-demo.js"
                 })
            Assert.DoesNotContain(forbidden, live, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTenantPage_MountsOrExposesALegacyTour()
    {
        // The component's own template is legacy infrastructure kept for the later cleanup; nothing may mount it.
        var componentTemplate = Path.Combine("Views", "Shared", "Components", "ProductTour") + Path.DirectorySeparatorChar;
        var views = Directory
            .EnumerateFiles(Path.Combine(Root(), "src", "Wasla.Web"), "*.cshtml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !path.Contains(componentTemplate, StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(views, path => path.EndsWith(Path.Combine("Tenant", "Views", "Dashboard", "Index.cshtml"), StringComparison.Ordinal));
        Assert.Contains(views, path => path.EndsWith(Path.Combine("Tenant", "Views", "Orders", "LiveDisplay.cshtml"), StringComparison.Ordinal));
        foreach (var view in views)
            AssertNoLegacyTour(File.ReadAllText(view), Path.GetRelativePath(Root(), view));
    }

    [Fact]
    public void HelpPage_CoversEveryRequiredTopic_WithoutTourControls()
    {
        var help = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml");
        string[] topics =
        [
            "help-trendyol", "help-yemeksepeti", "help-platform-manage", "help-print-bridge", "help-printer",
            "help-test-print", "help-live-screen", "help-approve", "help-prepare", "help-ready",
            "help-courier-pickup", "help-delivered", "help-delivered-window", "help-order-history", "help-troubleshooting"
        ];

        foreach (var topic in topics)
        {
            Assert.Contains($"<article id=\"{topic}\"", help, StringComparison.Ordinal);
            Assert.Contains($"(\"{topic}\",", help, StringComparison.Ordinal);
        }

        Assert.Contains("<h1", help, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"wasla-help__contents\" aria-labelledby=", help, StringComparison.Ordinal);
        Assert.Contains("Model.DeliveredWindowMinutes", help, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b2 (dakika|minutes)", RegexOptions.IgnoreCase), help);
        foreach (var forbidden in new[] { "ProductTour", "data-wasla-tour", "Tour.Replay", "wasla-tour", "Demo." })
            Assert.DoesNotContain(forbidden, help, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpNavigation_IsInTheSidebar_ForEveryTenantRole()
    {
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_TenantLayout.cshtml");
        var controller = typeof(HelpController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Matches(new Regex(@"@if \(navPermissions\.CanViewOrders\)\s*\{\s*<li class=""nav-item"">\s*<a class=""nav-link @Active\(""/help""\)"" asp-area=""Tenant"" asp-controller=""Help""", RegexOptions.Singleline), layout);
        Assert.Contains("@L[\"Help.Title\"]", layout, StringComparison.Ordinal);
        Assert.Equal(TenantPolicies.CanViewOrders, controller.Policy);
    }

    // Localization ------------------------------------------------------------------------

    [Fact]
    public void GuidedSetupAndHelpCopy_ExistsInEveryCulture_WithMatchingPlaceholders()
    {
        var byCulture = Cultures.ToDictionary(culture => culture, Load);
        var keys = byCulture[".en-US"].Keys
            .Where(key => key.StartsWith("GuidedSetup.", StringComparison.Ordinal) || key.StartsWith("Help.", StringComparison.Ordinal))
            .ToArray();
        Assert.True(keys.Length > 90);

        foreach (var key in keys)
        {
            var reference = Placeholders(byCulture[".en-US"][key]);
            foreach (var culture in Cultures)
            {
                Assert.True(byCulture[culture].TryGetValue(key, out var value), $"{key} missing in SharedResource{culture}.resx");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} empty in SharedResource{culture}.resx");
                Assert.Equal(reference, Placeholders(value!));
            }
        }
    }

    [Fact]
    public void EveryKeyUsedByTheNewViews_Exists()
    {
        var english = Load(".en-US");
        var sources = new[]
        {
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_GuidedSetupCard.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml"),
            Read("src", "Wasla.Web", "Models", "GuidedSetup", "GuidedSetupViewModels.cs"),
            Read("src", "Wasla.Web", "GuidedSetup", "GuidedSetupCoordinator.cs")
        };

        var used = sources
            .SelectMany(source => KeyPattern().Matches(source).Select(match => match.Groups[1].Value))
            .Where(key => !key.EndsWith('.'))
            // Section prefixes such as GuidedSetup.Platform are composed with a suffix below.
            .Where(key => english.ContainsKey(key) || !english.Keys.Any(existing => existing.StartsWith(key + ".", StringComparison.Ordinal)))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(used);
        foreach (var key in used)
            Assert.True(english.ContainsKey(key), $"{key} is used but not defined.");

        // Keys composed from a section prefix.
        foreach (var prefix in new[] { "GuidedSetup.Platform", "GuidedSetup.PrintBridge" })
        foreach (var suffix in new[] { ".Title", ".Body", ".Later", ".Ready", ".NotReady", ".SetUpLater" })
            Assert.True(english.ContainsKey(prefix + suffix), $"{prefix + suffix} is missing.");
    }

    [Fact]
    public void FirstUseCopy_MatchesTheProductText()
    {
        var turkish = Load(".tr-TR");

        Assert.Equal("Wasla'yı kullanıma hazırlayalım", turkish["GuidedSetup.FirstUse.Title"]);
        Assert.Equal("Rehberli kuruluma başla", turkish["GuidedSetup.Start"]);
        Assert.Equal("Rehberli kurulumu atla", turkish["GuidedSetup.Skip"]);
        Assert.Equal("Rehberli kurulumunuz yarım kaldı.", turkish["GuidedSetup.Resume.Title"]);
        Assert.Equal("Rehberli kuruluma devam et", turkish["GuidedSetup.Continue"]);
        Assert.Equal("Rehberli kurulumu sonlandır", turkish["GuidedSetup.End"]);
        Assert.StartsWith("Rehberli kurulumu atlarsanız uygulama içinde tekrar gösterilmez.", turkish["GuidedSetup.FirstUse.SkipConsequence"], StringComparison.Ordinal);
        Assert.Equal("Yardım ve Rehberler", turkish["Help.Title"]);
    }

    [Fact]
    public void ArabicDeliveredWindowCopy_IsNatural_AndTakesTheCanonicalDuration()
    {
        var arabic = Load(".ar-SA")["Help.DeliveredWindow.Body"];

        Assert.Equal("يبقى الطلب المكتمل ظاهرًا مؤقتًا قبل انتقاله إلى سجل الطلبات. المدة بالدقائق: {0}.", arabic);
        // No number is written into the copy; it comes only from LiveScreenVisibility.RecentDeliveredWindow.
        Assert.DoesNotMatch(new Regex(@"\d", RegexOptions.None), arabic.Replace("{0}", string.Empty, StringComparison.Ordinal));
        Assert.EndsWith(
            $"المدة بالدقائق: {(int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes}.",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, arabic, (int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes),
            StringComparison.Ordinal);
    }

    private GuidedSetupController Controller(FakeGuidedSetup state, string? userIdClaim = null)
    {
        var coordinator = new GuidedSetupCoordinator(state, new FixedNavigation(Owner), new FakeSetupStatus(), new FakeDemos(), new CapturingLogger());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userIdClaim ?? _userId.ToString())], "Tenant"))
        };
        return new GuidedSetupController(new FixedTenant(_tenantId), coordinator, new KeyLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };
    }

    /// <summary>No legacy tour component, key, replay launcher, coachmark script or tour skip dialog.</summary>
    private static void AssertNoLegacyTour(string view, string name)
    {
        foreach (var forbidden in new[]
                 {
                     "InvokeAsync(\"ProductTour\"", "<vc:product-tour", "ProductTourKeys", "suppressAutoStart",
                     "data-wasla-tour-replay", "wasla-tour.js", "WaslaTour.", "Tour.Replay", "waslaTourSkipModal"
                 })
            Assert.False(view.Contains(forbidden, StringComparison.Ordinal), $"{name} still contains the legacy tour marker '{forbidden}'.");
    }

    private static IEnumerable<MethodInfo> CommandMethods() =>
        typeof(GuidedSetupController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => typeof(Task<IActionResult>).IsAssignableFrom(method.ReturnType));

    private static string[] Placeholders(string value) =>
        PlaceholderPattern().Matches(value).Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

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

    [GeneratedRegex("\"((?:GuidedSetup|Help)\\.[A-Za-z.]+)\"")]
    private static partial Regex KeyPattern();

    private sealed class KeyLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class EmptyDashboard : Wasla.Application.Abstractions.Dashboard.IDashboardService
    {
        public Task<Wasla.Application.Abstractions.Dashboard.DashboardResult> GetTodayAsync(Guid customerId, CancellationToken ct) =>
            Task.FromResult(new Wasla.Application.Abstractions.Dashboard.DashboardResult());
    }

    private sealed class DenyAll : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Failed());
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
