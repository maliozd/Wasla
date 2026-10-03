using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.GuidedSetup;
using Wasla.Web.Models.Help;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.GuidedSetup;

public sealed partial class GuidedSetupWebTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);

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

        var result = await controller.Skip(origin: null, CancellationToken.None);

        // Skipping takes a Setup tenant live, so it opens the normal Live Screen.
        Assert.Equal("/orders/live-display", Assert.IsType<LocalRedirectResult>(result).Url);
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
        await end.End(confirmed: false, origin: null, CancellationToken.None);
        Assert.False(end.TempData.ContainsKey("SuccessDurationMs"));

        await end.End(confirmed: true, origin: null, CancellationToken.None);
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

        await controller.End(confirmed: false, origin: null, CancellationToken.None);
        Assert.Equal(GuidedSetupStatus.InProgress, state.Get(_userId).Status);

        await controller.End(confirmed: true, origin: null, CancellationToken.None);
        Assert.Equal(GuidedSetupStatus.Skipped, state.Get(_userId).Status);
    }

    /// <summary>
    /// Skip and End open the normal Live Screen whatever the form says: only the allow-listed origin is recognised, so a
    /// posted URL can never redirect anywhere else.
    /// </summary>
    [Theory]
    [InlineData("live-screen", "/orders/live-display")]
    [InlineData("https://evil.example/", "/orders/live-display")]
    [InlineData("//evil.example", "/orders/live-display")]
    [InlineData("/dashboard", "/orders/live-display")]
    [InlineData(null, "/orders/live-display")]
    public async Task SkipAndEnd_OpenTheLiveScreen_AndNeverFollowAPostedUrl(string? origin, string expected)
    {
        var skip = await Controller(new FakeGuidedSetup()).Skip(origin, CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<LocalRedirectResult>(skip).Url);

        var started = new FakeGuidedSetup();
        started.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);
        var controller = Controller(started);
        var end = await controller.End(confirmed: true, origin, CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<LocalRedirectResult>(end).Url);
        Assert.Equal(GuidedSetupStatus.Skipped, started.Get(_userId).Status);
        Assert.Equal(GuidedSetupController.ClosedToastDurationMs, controller.TempData["SuccessDurationMs"]);
    }

    [Fact]
    public void TrainingCommands_NeedOrderManagement_SoViewersCannotRunThem()
    {
        foreach (var name in new[] { nameof(GuidedSetupController.StartPractice), nameof(GuidedSetupController.CompleteTraining) })
        {
            var method = typeof(GuidedSetupController).GetMethod(name)!;
            Assert.Equal(TenantPolicies.CanManageOrders, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
    }

    [Fact]
    public async Task TrainingCompletion_ShowsTheLongerToast_AndReturnsToTheLiveScreen()
    {
        var state = new FakeGuidedSetup();
        state.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeReady);
        var demos = new FakeDemos { Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false) };
        var controller = Controller(state, demos: demos);

        var result = await controller.CompleteTraining(CancellationToken.None);

        Assert.Equal("/orders/live-display", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal(GuidedSetupCoordinator.TrainingCompletedMessageKey, controller.TempData["Success"]);
        Assert.Equal(GuidedSetupController.ClosedToastDurationMs, controller.TempData["SuccessDurationMs"]);
        Assert.Equal(GuidedSetupStatus.Completed, state.Get(_userId).Status);
    }

    [Fact]
    public async Task StartPractice_ReturnsToTheLiveScreen_WithoutAToast()
    {
        var state = new FakeGuidedSetup();
        state.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);
        var demos = new FakeDemos();
        var controller = Controller(state, demos: demos);

        var result = await controller.StartPractice(CancellationToken.None);

        Assert.Equal("/orders/live-display", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.False(controller.TempData.ContainsKey("Success"));
        Assert.Equal(1, demos.Created);
    }

    [Fact]
    public void LiveScreenPage_ReadsGuidanceOnAGet_AndDemoActionsRecordProgressOnAPost()
    {
        var liveDisplay = typeof(OrdersController).GetMethod(nameof(OrdersController.LiveDisplay))!;
        Assert.NotNull(liveDisplay.GetCustomAttribute<HttpGetAttribute>());
        Assert.Contains(liveDisplay.GetParameters(), parameter =>
            parameter.ParameterType == typeof(IGuidedSetupCoordinator) && parameter.GetCustomAttribute<FromServicesAttribute>() is not null);

        var act = typeof(GuidedDemoController).GetMethod(nameof(GuidedDemoController.Act))!;
        Assert.NotNull(act.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(act.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        // The practice order belongs to Owner-only order training.
        Assert.Equal(
            new[] { TenantPolicies.CanManageOrders, TenantPolicies.TenantOwner }.Order(StringComparer.Ordinal),
            typeof(GuidedDemoController).GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy!).Order(StringComparer.Ordinal));
        // The demo id comes from the route, but the user never does: ownership is checked against the signed-in user.
        Assert.DoesNotContain(act.GetParameters(), parameter => parameter.Name!.Contains("user", StringComparison.OrdinalIgnoreCase)
                                                              || parameter.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DashboardAccess_IsUnchanged_ByTheLiveScreenEntryPoint()
    {
        var dashboard = typeof(DashboardController).GetCustomAttributes<AuthorizeAttribute>().Select(attribute => attribute.Policy);

        Assert.Contains(TenantPolicies.CanViewReports, dashboard);
    }

    [Fact]
    public async Task Commands_UseOnlyTheAuthenticatedUser()
    {
        var state = new FakeGuidedSetup();
        var controller = Controller(state, userIdClaim: "not-a-guid");

        var result = await controller.Skip(origin: null, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(0, state.Writes);
        // No action takes a user id from the request.
        foreach (var method in CommandMethods())
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name!.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Commands_ArePostsWithAntiforgery_AndTheWholeControllerIsOwnerOnly()
    {
        var authorize = typeof(GuidedSetupController).GetCustomAttributes<AuthorizeAttribute>().ToArray();
        Assert.Equal(
            new[] { TenantPolicies.CanViewOrders, TenantPolicies.TenantOwner }.Order(StringComparer.Ordinal),
            authorize.Select(a => a.Policy!).Order(StringComparer.Ordinal));
        Assert.All(authorize, a => Assert.Equal(AuthSchemes.Tenant, a.AuthenticationSchemes));

        var methods = CommandMethods().ToArray();
        Assert.Equal(8, methods.Length);
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
        var controller = new HelpController(
            new FixedNavigation(kitchen),
            new DenyAll(),
            new FixedTenant(_tenantId),
            new Wasla.UnitTests.DevelopmentTools.TestHostEnvironment("Production"),
            Microsoft.Extensions.Options.Options.Create(new Wasla.Web.DevelopmentTools.DevelopmentToolsOptions()))
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
        Assert.False(model.ShowDevelopmentTools);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Dashboard_RendersTheCard_OrStillRendersWhenStateCannotBeRead(bool readFails, bool expectCard)
    {
        var state = new FakeGuidedSetup { FailReads = readFails };
        var logger = new CapturingLogger();
        var coordinator = new GuidedSetupCoordinator(state, new FixedNavigation(Owner), new FakeSetupStatus(), new FakeDemos(), new FakePrintBridgeDevices(), logger);
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
        var dialog = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupEndDialog.cshtml");

        foreach (var action in new[] { "/guided-setup/start", "/guided-setup/skip", "/guided-setup/continue" })
            Assert.Contains($"method=\"post\" action=\"{action}\"", card, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(card, "@Html.AntiForgeryToken\\(\\)").Count);
        Assert.Contains("@await Html.PartialAsync(\"_GuidedSetupEndDialog\", (string?)null)", card, StringComparison.Ordinal);
        Assert.Contains("method=\"post\" action=\"/guided-setup/end\"", dialog, StringComparison.Ordinal);
        Assert.Contains("@Html.AntiForgeryToken()", dialog, StringComparison.Ordinal);
        Assert.Contains("name=\"confirmed\" value=\"true\"", dialog, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"waslaGuidedSetupEndTitle\"", dialog, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"waslaGuidedSetupEndBody\"", dialog, StringComparison.Ordinal);
        Assert.Contains("data-bs-toggle=\"modal\" data-bs-target=\"#waslaGuidedSetupEndDialog\"", card, StringComparison.Ordinal);
        Assert.Contains("data-guided-setup-cancel", dialog, StringComparison.Ordinal);
        // Order training is a real next step now: the card opens it, and the Live Screen starts the practice order.
        Assert.Matches(new Regex(@"data-guided-setup-next-actions>\s*<form method=""post"" action=""/guided-setup/continue""", RegexOptions.Singleline), card);
        Assert.DoesNotContain("NotYetAvailable", card, StringComparison.Ordinal);
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
    public void LiveScreen_MountsTheNewTrainingPanel_InThePageFlow()
    {
        var live = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml");
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_OrdersDisplayLayout.cshtml");

        Assert.Matches(new Regex(@"@if \(Model\.GuidedTraining is not null\)\s*\{\s*@await Html\.PartialAsync\(""_GuidedTrainingPanel"", Model\.GuidedTraining\)\s*\}", RegexOptions.Singleline), live);
        // Above the orders, never over them.
        Assert.True(live.IndexOf("_GuidedTrainingPanel", StringComparison.Ordinal) < live.IndexOf("id=\"ordersLiveScreenHost\"", StringComparison.Ordinal));
        // Commands return here with a toast.
        Assert.Contains("<partial name=\"~/Views/Shared/_Notifications.cshtml\" />", layout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Live Screen's first-use variant (Start or Skip for users without the Dashboard) was unreachable once guided
    /// setup became Owner-only, because every Owner has the Dashboard; it and its exclusive hooks are gone. The Dashboard
    /// card keeps the shared first-use copy and styles.
    /// </summary>
    [Fact]
    public void TheLiveScreenFirstUseVariant_AndItsExclusiveHooks_AreGone()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_GuidedTrainingPanel.cshtml");
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-guided-training.css");
        var card = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_GuidedSetupCard.cshtml");

        foreach (var removed in new[] { "GuidedTrainingKind", "Model.Kind", "Model.Sections", "wasla-guided-training-first-use", "/guided-setup/start", "/guided-setup/skip", "name=\"origin\"" })
            Assert.DoesNotContain(removed, panel, StringComparison.Ordinal);
        Assert.DoesNotContain("wasla-guided-training-first-use", css, StringComparison.Ordinal);
        Assert.Null(typeof(GuidedTrainingViewModel).Assembly.GetType("Wasla.Web.Models.GuidedSetup.GuidedTrainingKind"));
        Assert.Null(typeof(GuidedTrainingViewModel).GetProperty("Kind"));
        Assert.Null(typeof(GuidedTrainingViewModel).GetProperty("Sections"));

        // The Owner's first-use decision lives on the Dashboard card, which still uses the shared copy.
        foreach (var key in new[] { "GuidedSetup.FirstUse.Title", "GuidedSetup.FirstUse.Body", "GuidedSetup.FirstUse.SkipConsequence" })
            Assert.Contains(key, card, StringComparison.Ordinal);
        Assert.Contains("action=\"/guided-setup/skip\"", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Dashboard card has one first-use presentation: guided setup is Owner-only and the Owner may act on every
    /// section, so the order-training-only copy could never show and is gone with its view-model flag and resource.
    /// </summary>
    [Fact]
    public void DashboardCard_HasOneFirstUsePresentation_WithStartAndSkip()
    {
        var card = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_GuidedSetupCard.cshtml");

        foreach (var removed in new[] { "IsOrderTrainingOnly", "BodyOrderTraining" })
            Assert.DoesNotContain(removed, card, StringComparison.Ordinal);
        Assert.Null(typeof(GuidedSetupDashboardViewModel).GetProperty("IsOrderTrainingOnly"));
        Assert.Equal(["Kind", "Sections", "CurrentSectionKey"],
            typeof(GuidedSetupDashboardViewModel).GetProperties().Where(p => p.Name != "EqualityContract").Select(p => p.Name));
        foreach (var culture in Cultures)
            Assert.False(Load(culture).ContainsKey("GuidedSetup.FirstUse.BodyOrderTraining"), $"SharedResource{culture}.resx");

        var firstUse = Regex.Match(card, @"@if \(Model\.Kind == GuidedSetupCardKind\.FirstUse\)\s*\{(?<body>[\s\S]*?)\n        \}\s*else if").Groups["body"].Value;
        Assert.NotEmpty(firstUse);
        Assert.Single(Regex.Matches(firstUse, "wasla-guided-setup-card__body"));
        Assert.Contains("<p class=\"wasla-guided-setup-card__body\">@L[\"GuidedSetup.FirstUse.Body\"]</p>", firstUse, StringComparison.Ordinal);
        Assert.DoesNotContain("?", Regex.Match(firstUse, "<p class=\"wasla-guided-setup-card__body\">.*?</p>").Value, StringComparison.Ordinal);
        Assert.Contains("method=\"post\" action=\"/guided-setup/start\"", firstUse, StringComparison.Ordinal);
        Assert.Contains("method=\"post\" action=\"/guided-setup/skip\"", firstUse, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(firstUse, "@Html.AntiForgeryToken\\(\\)").Count);
        Assert.Contains("aria-describedby=\"waslaGuidedSetupSkipNote\"", firstUse, StringComparison.Ordinal);
        foreach (var key in new[] { "GuidedSetup.FirstUse.Title", "GuidedSetup.FirstUse.Body", "GuidedSetup.FirstUse.SkipConsequence", "GuidedSetup.Start", "GuidedSetup.Skip" })
            foreach (var culture in Cultures)
                Assert.False(string.IsNullOrWhiteSpace(Load(culture).GetValueOrDefault(key)), $"{key} in SharedResource{culture}.resx");
    }

    [Fact]
    public void TrainingPanel_UsesPostCommands_AndNeverOffersCourierOrLegacyControls()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_GuidedTrainingPanel.cshtml");

        foreach (var action in new[] { "/guided-setup/training/practice", "/guided-setup/training/complete" })
            Assert.Contains($"method=\"post\" action=\"{action}\" data-guided-setup-form", panel, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(panel, "@Html.AntiForgeryToken\\(\\)").Count);
        // End uses the shared confirmation dialog and returns to the Live Screen.
        Assert.Contains("@await Html.PartialAsync(\"_GuidedSetupEndDialog\", GuidedSetupController.LiveScreenOrigin)", panel, StringComparison.Ordinal);
        // Start practice only on the introduction, Complete only after delivery.
        Assert.Contains("data-training-when=\"@GuidedTrainingSteps.Intro\" hidden=\"@HiddenFor(GuidedTrainingSteps.Intro)\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-training-when=\"@GuidedTrainingSteps.PracticeDelivered\" hidden=\"@HiddenFor(GuidedTrainingSteps.PracticeDelivered)\"", panel, StringComparison.Ordinal);
        // Worker-driven changes are announced politely; Escape is advertised on the hide control.
        Assert.Contains("role=\"status\" aria-live=\"polite\" data-training-announce", panel, StringComparison.Ordinal);
        Assert.Contains("aria-keyshortcuts=\"Escape\"", panel, StringComparison.Ordinal);
        // The delivered window comes from the canonical value.
        Assert.Contains("Model.DeliveredWindowMinutes", panel, StringComparison.Ordinal);
        AssertNoLegacyTour(panel, "Live Screen training panel");
        foreach (var forbidden in new[] { "/orders/demo", "hand-to-courier", "mark-delivered", "on-the-way\"", "wasla-demo", "confirm(" })
            Assert.DoesNotContain(forbidden, panel, StringComparison.Ordinal);
    }

    [Fact]
    public void TrainingStyles_RespectReducedMotion_AndUseLogicalProperties()
    {
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-guided-training.css");

        Assert.Matches(new Regex(@"@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.wasla-guided-training-target--action\s*\{\s*animation: none;", RegexOptions.Singleline), css);
        foreach (var physical in new[] { "margin-left", "margin-right", "padding-left", "padding-right", "border-left", "border-right", " left:", " right:", "position: fixed", "position: absolute" })
            Assert.DoesNotContain(physical, css, StringComparison.Ordinal);
        // No fixed widths that could force horizontal scrolling at 375 px.
        Assert.DoesNotMatch(new Regex(@"(?<!max-)(?<!min-)(inline-size|width):\s*\d{3,}px"), css);
    }

    [Fact]
    public void TrainingCopy_UsesTheCanonicalDeliveredWindow_AndNamesTheRealButtons()
    {
        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            var delivered = resources["GuidedSetup.Training.Step.Delivered.Body"];
            Assert.Contains("{0}", delivered, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\d"), delivered.Replace("{0}", string.Empty, StringComparison.Ordinal));
            Assert.Contains("{1}", resources["GuidedSetup.Training.Step.New.Body"], StringComparison.Ordinal);
            Assert.Contains("{2}", resources["GuidedSetup.Training.Step.Accepted.Body"], StringComparison.Ordinal);
            Assert.Contains("{3}", resources["GuidedSetup.Training.Step.Preparing.Body"], StringComparison.Ordinal);
            Assert.Contains("{4}", resources["GuidedSetup.Training.Step.Ready.Body"], StringComparison.Ordinal);
            Assert.Contains("{5}", resources["GuidedSetup.Training.Step.OnTheWay.Body"], StringComparison.Ordinal);
            Assert.Contains("{6}", resources["GuidedSetup.Training.Step.Intro.Body"], StringComparison.Ordinal);
            Assert.False(resources.ContainsKey("GuidedSetup.Next.NotYetAvailable"), culture);
        }

        var english = Load(".en-US");
        Assert.Contains("Trendyol GO", english["GuidedSetup.Training.Step.Intro.Body"], StringComparison.Ordinal);
        Assert.Contains("order history", english["GuidedSetup.Training.Step.Delivered.Body"], StringComparison.Ordinal);
        Assert.Contains("practice order is not saved", english["GuidedSetup.Training.Step.Delivered.Body"], StringComparison.Ordinal);
        Assert.Contains("restaurant's last step", english["GuidedSetup.Training.Step.Preparing.Body"], StringComparison.Ordinal);
        Assert.Contains("automatically", english["GuidedSetup.Training.Step.Ready.Body"], StringComparison.Ordinal);
        Assert.Contains("Help and Guides", english["GuidedSetup.Training.Completed"], StringComparison.Ordinal);
    }

    // Print Bridge: connected state ---------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SectionStatus_IsAReadOnlyGet_ThatReportsTheChecklistsReadiness(bool connected)
    {
        var state = new FakeGuidedSetup();
        state.Set(_userId, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        var coordinator = new GuidedSetupCoordinator(state, new FixedNavigation(Owner), new FakeSetupStatus { PrintingReady = connected }, new FakeDemos(), new FakePrintBridgeDevices(), new CapturingLogger());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Tenant"))
        };
        var controller = new GuidedSetupController(new FixedTenant(_tenantId), coordinator, new KeyLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };

        var result = Assert.IsType<OkObjectResult>(await controller.SectionStatus(GuidedSetupSections.PrintBridge, CancellationToken.None));

        Assert.Equal(JsonSerializer.Serialize(new { current = true, ready = connected }), JsonSerializer.Serialize(result.Value));
        Assert.Equal(0, state.Writes);
        var method = typeof(GuidedSetupController).GetMethod(nameof(GuidedSetupController.SectionStatus))!;
        Assert.NotNull(method.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(method.GetCustomAttribute<HttpPostAttribute>());
    }

    [Fact]
    public void SectionContinue_IsASeparateAntiforgeryPost_FromSetUpLater()
    {
        var continueMethod = typeof(GuidedSetupController).GetMethod(nameof(GuidedSetupController.ContinueFromSection))!;
        var laterMethod = typeof(GuidedSetupController).GetMethod(nameof(GuidedSetupController.Advance))!;

        Assert.Equal("section-continue", continueMethod.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal("advance", laterMethod.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(continueMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public void SectionPanel_RendersSeparateUnconnectedAndConnectedStates()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml");
        var notReady = Block(panel, "<div class=\"wasla-guided-setup-panel__state\" data-guided-setup-when=\"not-ready\" hidden=\"@ready\">", "</div>");
        var ready = Block(panel, "<div class=\"wasla-guided-setup-panel__state\" data-guided-setup-when=\"ready\" hidden=\"@(!ready)\">", "</div>");

        // Unconnected: the platform section links to its form; Print Bridge has no top action (its first-install steps
        // below the panel are the way in). Both can defer.
        Assert.Matches(new Regex(@"@if \(isPlatform\)\s*\{\s*<a class=""btn btn-primary"" href=""/platform-connections/create"">@L\[""PlatformConnections\.Create""\]</a>\s*\}\s*@\*"), notReady);
        Assert.DoesNotContain("SetUpNow", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"#", notReady, StringComparison.Ordinal);
        Assert.Contains("action=\"/guided-setup/advance\"", notReady, StringComparison.Ordinal);
        Assert.Contains("@L[prefix + \".SetUpLater\"]", notReady, StringComparison.Ordinal);
        // Connected: the platform section continues through its own POST; a connected Print Bridge first opens its
        // device guide (which holds that POST). No "set up later", no unconnected copy.
        Assert.Contains("action=\"/guided-setup/section-continue\"", ready, StringComparison.Ordinal);
        Assert.Contains("@L[GuidedSetupCopy.ContinueToKey(Model.NextSectionKey)]", ready, StringComparison.Ordinal);
        Assert.Contains("href=\"@GuidedSetupCoordinator.DeviceGuideEntryUrl\" data-guided-setup-continue>@L[\"GuidedSetup.PrintBridge.MeetDevice\"]</a>", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("SetUpLater", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("NotReady", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("/guided-setup/advance", ready, StringComparison.Ordinal);
        // The unconnected status text is only in the not-ready element.
        Assert.Matches(new Regex(@"data-guided-setup-when=""not-ready"" hidden=""@ready"">\s*<i class=""bi bi-info-circle"" aria-hidden=""true""></i>\s*<span>@L\[prefix \+ "".NotReady""\]</span>"), panel);
        Assert.Matches(new Regex(@"data-guided-setup-when=""ready"" hidden=""@\(!ready\)"">\s*<i class=""bi bi-check-circle-fill"" aria-hidden=""true""></i>\s*<span>@L\[prefix \+ "".Ready""\]</span>"), panel);
        Assert.Contains("<li data-guided-setup-when=\"not-ready\" hidden=\"@ready\">@L[prefix + \".Later\"]</li>", panel, StringComparison.Ordinal);
        // Only Print Bridge refreshes in place: after its automatic flow reports success, or while a manual connection is pending.
        Assert.Contains("data-guided-setup-refresh-on=\"@(isPlatform ? null : \"wasla:print-bridge-setup-completed\")\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-guided-setup-watch-on=\"@(isPlatform ? null : \"wasla:print-bridge-manual-setup-started\")\"", panel, StringComparison.Ordinal);
        Assert.Contains("data-guided-setup-status-url=\"/guided-setup/section-status?section=@Model.SectionKey\"", panel, StringComparison.Ordinal);
        Assert.Equal("GuidedSetup.Panel.ContinueToOrderTraining", Wasla.Web.Models.GuidedSetup.GuidedSetupCopy.ContinueToKey(GuidedSetupSections.LiveScreenDemo));
        Assert.Equal("GuidedSetup.Panel.ContinueToPrintBridge", Wasla.Web.Models.GuidedSetup.GuidedSetupCopy.ContinueToKey(GuidedSetupSections.PrintBridge));
    }

    // Print Bridge connected: the panel's connected state, fixed to the viewport -------------------------------------

    /// <summary>
    /// The fixed panel is one more "ready" part of the Print Bridge section panel: it exists only while the Owner is
    /// InProgress at the current Print Bridge section (the panel's own lifecycle), shows only once the server has
    /// confirmed the device, and offers exactly the panel's own device-guide link.
    /// </summary>
    [Fact]
    public void ConnectedDock_IsAServerConfirmedPartOfThePrintBridgePanel_WithTheSameDeviceGuideLink()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml");
        var dock = Block(panel, "<aside class=\"wasla-guided-setup-dock\"", "</aside>");

        // Print Bridge only, inside the section panel.
        Assert.Matches(new Regex(@"@if \(!isPlatform\)\s*\{\s*@\*.*?\*@\s*<aside class=""wasla-guided-setup-dock""", RegexOptions.Singleline), panel);
        Assert.True(panel.IndexOf("<aside class=\"wasla-guided-setup-dock\"", StringComparison.Ordinal) < panel.LastIndexOf("</section>", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(panel, "<aside "));
        // Hidden until the server has confirmed the device (rendered readiness, then the panel's read-only re-check).
        Assert.Contains("data-guided-setup-when=\"ready\" data-guided-setup-dock hidden=\"@(!ready)\"", dock, StringComparison.Ordinal);

        // The same action twice: the device-guide GET with the existing label and the shared continuation contract.
        var links = Regex.Matches(panel, "<a [^>]*data-guided-setup-continue[^>]*>([^<]*)</a>");
        Assert.Equal(2, links.Count);
        Assert.All(links, link =>
        {
            Assert.Contains("href=\"@GuidedSetupCoordinator.DeviceGuideEntryUrl\"", link.Value, StringComparison.Ordinal);
            Assert.Equal("@L[\"GuidedSetup.PrintBridge.MeetDevice\"]", link.Groups[1].Value);
        });
        foreach (var transition in new[] { "<form", "method=\"post\"", "formaction", "/guided-setup/section-continue", "/guided-setup/advance" })
            Assert.DoesNotContain(transition, dock, StringComparison.Ordinal);

        // Its copy, and nothing about the device: no token, setup code, id or model value.
        Assert.Contains("<p id=\"waslaGuidedSetupDockTitle\" class=\"wasla-guided-setup-dock__title\">@L[\"GuidedSetup.PrintBridge.Dock.Title\"]</p>", dock, StringComparison.Ordinal);
        Assert.Contains("<p class=\"wasla-guided-setup-dock__body\">@L[\"GuidedSetup.PrintBridge.Dock.Body\"]</p>", dock, StringComparison.Ordinal);
        foreach (var secret in new[] { "Model.", "token", "Token", "code", "Code", "DeviceId", "deviceId", "data-device", "AntiForgery" })
            Assert.DoesNotContain(secret, dock, StringComparison.Ordinal);

        // Persistent and calm: no close button or toast, and no live region of its own; the panel's single status line
        // is the one polite announcement of the connection.
        foreach (var forbidden in new[] { "btn-close", "data-bs-dismiss", "toast", "role=\"status\"", "role=\"alert\"", "aria-live", "autofocus", "tabindex" })
            Assert.DoesNotContain(forbidden, dock, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(panel, "role=\"status\""));
        Assert.Contains("aria-labelledby=\"waslaGuidedSetupDockTitle\"", dock, StringComparison.Ordinal);
        Assert.Contains("<i class=\"bi bi-check-circle-fill wasla-guided-setup-dock__icon\" aria-hidden=\"true\"></i>", dock, StringComparison.Ordinal);
        var ids = Regex.Matches(panel, "\\sid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ConnectedDock_IsFixed_Responsive_RightToLeft_Dark_Focusable_AndMotionFree()
    {
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-guided-setup.css");
        var dock = css[css.IndexOf("/* Print Bridge connected:", StringComparison.Ordinal)..];

        // Desktop: bottom-inline-end of the viewport, on the theme's surface, border and shadow.
        Assert.Matches(new Regex(@"\.wasla-guided-setup-dock \{[^}]*position: fixed;[^}]*inset-block-end: 1\.5rem;[^}]*inset-inline-end: 1\.5rem;[^}]*border: 1px solid var\(--wasla-border[^}]*background: var\(--wasla-surface[^}]*box-shadow:"), dock);
        Assert.Matches(new Regex(@"\.wasla-guided-setup-dock\[hidden\] \{\s*display: none !important;\s*\}"), dock);
        // It never covers the last controls on the page, and focused elements scroll clear of it.
        Assert.Contains("body:has(.wasla-guided-setup-dock:not([hidden]))", dock, StringComparison.Ordinal);
        Assert.Contains("scroll-padding-block-end", dock, StringComparison.Ordinal);
        // Phones: across the bottom, above the home indicator, wrapping text and button.
        var phone = Regex.Match(dock, @"@media \(max-width: 575\.98px\) \{(?<body>[\s\S]*)\}\s*$").Groups["body"].Value;
        foreach (var rule in new[] { "inset-block-end: 0;", "inset-inline: 0;", "flex-wrap: wrap;", "env(safe-area-inset-bottom, 0px)", "white-space: normal;", "flex: 1 1 100%;" })
            Assert.Contains(rule, phone, StringComparison.Ordinal);
        // Dark mode, a visible keyboard focus ring, and the waiting state of the shared links.
        Assert.Contains("[data-bs-theme=\"dark\"] .wasla-guided-setup-dock", dock, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\.wasla-guided-setup-dock \.btn:focus-visible \{\s*outline: 2px solid var\(--wasla-focus"), dock);
        Assert.Contains("[data-guided-setup-continue][aria-disabled=\"true\"]", dock, StringComparison.Ordinal);
        // Logical properties only (Arabic mirrors it), and no motion at all.
        foreach (var physical in new[] { "margin-left", "margin-right", "padding-left", "padding-right", "border-left", "border-right", " left:", " right:", "text-align: left", "text-align: right", "top-left", "top-right" })
            Assert.DoesNotContain(physical, dock, StringComparison.Ordinal);
        foreach (var motion in new[] { "animation", "transition", "@keyframes", "transform" })
            Assert.DoesNotContain(motion, dock, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedDockCopy_IsLocalizedEverywhere_AndReusesTheButtonLabel()
    {
        foreach (var turkish in new[] { "", ".tr-TR" })
        {
            Assert.Equal("Print Bridge bağlandı", Load(turkish)["GuidedSetup.PrintBridge.Dock.Title"]);
            Assert.Equal("Kuruluma devam etmek için cihazınızı tanıyın.", Load(turkish)["GuidedSetup.PrintBridge.Dock.Body"]);
            // "Cihazını tanı" stays one shared key.
            Assert.Single(Load(turkish), entry => entry.Value == "Cihazını tanı");
        }

        var manager = Resources();
        foreach (var culture in Cultures)
        {
            var values = Load(culture);
            foreach (var key in new[] { "GuidedSetup.PrintBridge.Dock.Title", "GuidedSetup.PrintBridge.Dock.Body", "GuidedSetup.PrintBridge.MeetDevice" })
            {
                Assert.False(string.IsNullOrWhiteSpace(values.GetValueOrDefault(key)), $"{key} in SharedResource{culture}.resx");
                Assert.DoesNotMatch(@"\{\d+\}", values[key]);
                if (culture is ".en-US" or ".ar-SA" or ".ru-RU")
                    Assert.NotEqual(Load(".tr-TR")[key], values[key]);
            }

            // The rendered text resolves from each culture's satellite, never as a key.
            var info = culture.Length == 0 ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo(culture[1..]);
            foreach (var key in new[] { "GuidedSetup.PrintBridge.Dock.Title", "GuidedSetup.PrintBridge.Dock.Body" })
                Assert.Equal(values[key], manager.GetString(key, info));
        }
        Assert.Matches(@"\p{IsArabic}", Load(".ar-SA")["GuidedSetup.PrintBridge.Dock.Body"]);
    }

    [Fact]
    public void ConnectedDock_NeverScrolls_Navigates_OrMovesTheJourney_OnItsOwn()
    {
        var guidedJs = Read("src", "Wasla.Web", "wwwroot", "js", "wasla-guided-setup.js");

        // (The only focus call stays the End dialog's safe choice.)
        Assert.Single(Regex.Matches(guidedJs, @"\.focus\(\)"));
        foreach (var forbidden in new[] { "scroll", "IntersectionObserver", "location", ".click(", "method: \"POST\"", ".submit(", "setInterval" })
            Assert.DoesNotContain(forbidden, guidedJs, StringComparison.Ordinal);
        // Both visible links share one guard; the panel state code is what shows the fixed panel.
        Assert.Single(Regex.Matches(guidedJs, "var CONTINUE_SELECTOR = \"a\\[data-guided-setup-continue\\]\";"));
        Assert.Contains("doc.addEventListener(\"click\", function (event) { handleContinueClick(event, doc, win); });", guidedJs, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintBridgeSetupPage_AnnouncesSuccess_WithoutMovingTheJourney()
    {
        var setupJs = Read("src", "Wasla.Web", "wwwroot", "js", "print-bridge", "print-bridge-setup.js");
        var guidedJs = Read("src", "Wasla.Web", "wwwroot", "js", "wasla-guided-setup.js");

        // One canonical notification, sent only for a verified completion of the automatic flow.
        Assert.Matches(new Regex(@"function notifySetupCompleted\(data\) \{\s*if \(!data \|\| data\.status !== ""Completed"" \|\| !data\.connectionVerified\) return;\s*document\.dispatchEvent\(new CustomEvent\(""wasla:print-bridge-setup-completed"""), setupJs);
        Assert.Single(Regex.Matches(setupJs, "wasla:print-bridge-setup-completed"));
        Assert.Single(Regex.Matches(setupJs, @"case ""Completed"":[^}]*?notifySetupCompleted\(data\);"));
        // A manual connection only starts the panel's readiness watch; issuing a token claims nothing.
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupSectionPanel.cshtml");
        Assert.Single(Regex.Matches(setupJs, "wasla:print-bridge-manual-setup-started"));
        Assert.Contains("data-guided-setup-watch-on=\"@(isPlatform ? null : \"wasla:print-bridge-manual-setup-started\")\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("notifySetupCompleted", setupJs[setupJs.IndexOf("function showManualToken", StringComparison.Ordinal)..], StringComparison.Ordinal);
        // Continue stays an explicit, server-verified POST in the panel.
        Assert.Contains("<form method=\"post\" action=\"/guided-setup/section-continue\" data-guided-setup-form>", panel, StringComparison.Ordinal);
        // The panel only reads: no posting, no navigation, no endless polling.
        foreach (var forbidden in new[] { "method: \"POST\"", ".submit(", "location.href", "location.assign", "location.replace", "setInterval" })
            Assert.DoesNotContain(forbidden, guidedJs, StringComparison.Ordinal);
        // The manual-connection watch is bounded and ends when the page is hidden.
        Assert.Matches(new Regex(@"var WATCH_ATTEMPTS = \d+;"), guidedJs);
        Assert.Contains("win.addEventListener(\"pagehide\"", guidedJs, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinueToOrderTraining_IsLocalizedEverywhere()
    {
        Assert.Equal("Sipariş eğitimine devam et", Load(".tr-TR")["GuidedSetup.Panel.ContinueToOrderTraining"]);
        Assert.Equal("Continue to order training", Load(".en-US")["GuidedSetup.Panel.ContinueToOrderTraining"]);
        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in new[] { "GuidedSetup.Panel.ContinueToOrderTraining", "GuidedSetup.Panel.ContinueToPrintBridge", "GuidedSetup.Panel.NotReadyYet" })
                Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{key} missing in {culture}");
        }
    }

    // Turkish capitalization -------------------------------------------------------------------

    [Fact]
    public void TurkishSectionName_IsSiparisEgitimi_Everywhere()
    {
        foreach (var culture in new[] { "", ".tr-TR" })
        {
            var turkish = Load(culture);
            Assert.Equal("Sipariş eğitimi", turkish["GuidedSetup.Section.LiveScreenDemo"]);
            Assert.Equal("Sıradaki bölüm: Sipariş eğitimi", turkish["GuidedSetup.Next.Title"]);
            Assert.Equal("Sipariş eğitimi: Canlı Ekran", turkish["GuidedSetup.Training.Step.Intro.Title"]);
            Assert.Equal("Sipariş eğitimini aç", turkish["GuidedSetup.Next.Open"]);
            // Never as a lowercase section name after a label or at the start of a heading.
            foreach (var (key, value) in turkish.Where(pair => pair.Key.StartsWith("GuidedSetup.", StringComparison.Ordinal)))
            {
                Assert.False(value.StartsWith("sipariş eğitimi", StringComparison.Ordinal), key);
                Assert.DoesNotContain(": sipariş eğitimi", value, StringComparison.Ordinal);
            }
        }

        // Other languages keep their own sentence case.
        Assert.Equal("Next: order training", Load(".en-US")["GuidedSetup.Next.Title"]);
    }

    // Real orders during training: counted, never shown, never pausing --------------------------

    [Fact]
    public void TrainingPanel_ReportsRealOrders_InOnePoliteStatusLine_BelowTheTrainingActions()
    {
        var panel = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_GuidedTrainingPanel.cshtml");
        var status = Block(panel, "data-training-actions>", "data-training-when=\"@courierSteps\"");

        // One live region that stays in the page; its box is hidden (no banner) until the snapshot reports an order.
        Assert.Contains("<div class=\"wasla-guided-training__real-orders\" role=\"status\" aria-live=\"polite\" aria-atomic=\"true\" data-training-real-orders>", status, StringComparison.Ordinal);
        Assert.Contains("data-training-real-orders-box hidden>", status, StringComparison.Ordinal);
        Assert.Contains("data-training-real-orders-title", status, StringComparison.Ordinal);
        Assert.Contains("data-training-real-orders-body", status, StringComparison.Ordinal);
        Assert.True(panel.IndexOf("data-training-real-orders", StringComparison.Ordinal) > panel.IndexOf("wasla-guided-training__end", StringComparison.Ordinal),
            "the status line comes after the training actions");
        Assert.DoesNotContain("tabindex", Block(panel, "data-training-real-orders>", "data-training-when=\"@courierSteps\""), StringComparison.Ordinal);
        foreach (var key in new[] { "TitleOne", "TitleMany", "BodyOne", "BodyMany" })
            Assert.Contains($"L[\"GuidedSetup.Training.RealOrders.{key}\"]", panel, StringComparison.Ordinal);

        // The real-order pause and its controls are gone.
        foreach (var removed in new[] { "data-training-pause", "data-training-resume", "data-training-show-active", "data-training-normal", "GuidedSetup.Training.Paused", "GuidedSetup.Training.ShowActive", "GuidedSetup.Training.Resume" })
            Assert.DoesNotContain(removed, panel, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"data-training-action(?!s)"), panel);
        Assert.Matches(new Regex(@"<button type=""button"" class=""btn btn-link wasla-guided-training__end"" data-bs-toggle=""modal""\s+data-bs-target=""#waslaGuidedSetupEndDialog"">"), panel);
    }

    [Fact]
    public void RealOrdersStatus_IsStyledWithLogicalProperties_WrapsOnAPhone_AndThePauseStylesAreGone()
    {
        var css = Read("src", "Wasla.Web", "wwwroot", "css", "wasla-guided-training.css");
        var layout = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_OrdersDisplayLayout.cshtml");
        var status = Block(css, ".wasla-guided-training__real-orders-box {", ".wasla-guided-training__actions {");

        // Logical properties only, so Arabic mirrors through the layout's dir="rtl" without overrides.
        foreach (var physical in new[] { "left", "right", "float", "position:" })
            Assert.DoesNotContain(physical, status, StringComparison.Ordinal);
        Assert.Contains("min-inline-size: 0;", status, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(?<!max-)(inline-size|width):\s*\d+(px|rem)"), status);
        // Quieter than the training action: small, secondary text, no button.
        Assert.Contains("font-size: 0.8rem;", status, StringComparison.Ordinal);
        Assert.DoesNotContain("btn", Block(Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_GuidedTrainingPanel.cshtml"),
            "data-training-real-orders>", "data-training-when=\"@courierSteps\""), StringComparison.Ordinal);
        foreach (var removed in new[] { "is-paused", "__pause" })
            Assert.DoesNotContain(removed, css, StringComparison.Ordinal);
        Assert.Contains("dir=", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderTraining_AddsNoRealtimeTransport_TheLiveScreenStillPolls()
    {
        var source = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal)
                || path.EndsWith(".cshtml", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path => !Regex.IsMatch(path, @"[\\/](bin|obj|lib)[\\/]"));
        foreach (var path in source)
        {
            var text = File.ReadAllText(path);
            foreach (var transport in new[] { "AddSignalR", "MapHub", "HubConnectionBuilder", "Microsoft.AspNetCore.SignalR", "new EventSource", "new WebSocket", "text/event-stream" })
                Assert.False(text.Contains(transport, StringComparison.Ordinal), $"{transport} in {Path.GetFileName(path)}");
        }

        Assert.Contains("liveDataUrl: \"/orders/live-data\"", Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void RealOrdersCopy_IsTheAgreedTurkish_AndEveryCultureHasASingularAndAPlural()
    {
        foreach (var turkish in new[] { Load(""), Load(".tr-TR") })
        {
            Assert.Equal("Siz eğitimdeyken gerçek bir sipariş geldi", turkish["GuidedSetup.Training.RealOrders.TitleOne"]);
            Assert.Equal("Siz eğitimdeyken {0} gerçek sipariş geldi", turkish["GuidedSetup.Training.RealOrders.TitleMany"]);
            Assert.Equal("Eğitime devam edebilirsiniz. Siparişi platform panelinizden yönetebilirsiniz. Eğitim tamamlandığında aktif siparişler Canlı Ekran'da gösterilecektir.", turkish["GuidedSetup.Training.RealOrders.BodyOne"]);
            Assert.Equal("Eğitime devam edebilirsiniz. Siparişleri platform panelinizden yönetebilirsiniz. Eğitim tamamlandığında aktif siparişler Canlı Ekran'da gösterilecektir.", turkish["GuidedSetup.Training.RealOrders.BodyMany"]);
        }

        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            Assert.DoesNotContain("{", resources["GuidedSetup.Training.RealOrders.TitleOne"], StringComparison.Ordinal);
            Assert.Single(PlaceholderPattern().Matches(resources["GuidedSetup.Training.RealOrders.TitleMany"]));
            Assert.Contains("{0}", resources["GuidedSetup.Training.RealOrders.TitleMany"], StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\d"), resources["GuidedSetup.Training.RealOrders.TitleMany"].Replace("{0}", string.Empty, StringComparison.Ordinal));
            foreach (var body in new[] { "BodyOne", "BodyMany" })
            {
                Assert.DoesNotContain("{", resources["GuidedSetup.Training.RealOrders." + body], StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(resources["GuidedSetup.Training.RealOrders." + body]));
            }

            // The pause copy is gone everywhere.
            foreach (var removed in new[] { "PausedTitle", "PausedOne", "PausedMany", "ShowActiveOne", "ShowActiveMany", "Resume" })
                Assert.False(resources.ContainsKey("GuidedSetup.Training." + removed), $"GuidedSetup.Training.{removed} still in SharedResource{culture}.resx");
        }
    }

    // Neutral-culture fallback ------------------------------------------------------------------

    [Fact]
    public void TrainingLabels_ResolveToText_InTheNeutralCultureAndEveryResourceFile()
    {
        var labels = new[] { "Orders.Approve", "Orders.StartPreparing", "Orders.MarkReady", "OrderStatus.OnTheWay", "OrderStatus.Delivered", "Demo.Badge", "Help.Title" };
        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var label in labels)
                Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(label)), $"{label} missing in SharedResource{culture}.resx");
        }

        // The neutral resources are what the resource manager falls back to.
        var manager = Resources();
        Assert.Equal("Onayla", manager.GetString("Orders.Approve", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Teslim Edildi", manager.GetString("OrderStatus.Delivered", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    public void RenderedTrainingCopy_NeverShowsResourceKeys_AndTakesTheCanonicalWindow(string cultureName)
    {
        var culture = cultureName.Length == 0 ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo(cultureName);
        var manager = Resources();
        string Text(string key) => manager.GetString(key, culture) ?? key;
        var minutes = (int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes;
        // The same arguments _GuidedTrainingPanel.cshtml passes.
        object[] args =
        [
            minutes, Text("Orders.Approve"), Text("Orders.StartPreparing"), Text("Orders.MarkReady"),
            Text("OrderStatus.OnTheWay"), Text("OrderStatus.Delivered"), Text("Demo.Badge")
        ];

        foreach (var step in GuidedTrainingSteps.All)
        {
            var prefix = Wasla.Web.Models.GuidedSetup.GuidedSetupCopy.TrainingStepKey(step);
            foreach (var rendered in new[]
                     {
                         Text(prefix + ".Title"),
                         string.Format(culture, Text(prefix + ".Body"), args),
                         string.Format(culture, Text("GuidedSetup.Training.Progress"), GuidedTrainingSteps.IndexOf(step) + 1, GuidedTrainingSteps.All.Count)
                     })
            {
                foreach (var fragment in new[] { "Orders.", "OrderStatus.", "Demo.", "GuidedSetup.", "{" })
                    Assert.False(rendered.Contains(fragment, StringComparison.Ordinal), $"{cultureName}/{step}: '{rendered}' contains '{fragment}'");
            }
        }

        var delivered = string.Format(culture, Text("GuidedSetup.Training.Step.Delivered.Body"), args);
        Assert.Contains(minutes.ToString(culture), delivered, StringComparison.Ordinal);
    }

    private static System.Resources.ResourceManager Resources() =>
        new("Wasla.Web.Resources.SharedResource", typeof(SharedResource).Assembly);

    /// <summary>The text from <paramref name="start"/> up to the next <paramref name="end"/>.</summary>
    private static string Block(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
        return source[from..to];
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
            "help-trendyol", "help-yemeksepeti", "help-platform-manage", "help-print-bridge", "help-print-bridge-devices", "help-printer",
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
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_GuidedSetupEndDialog.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "_GuidedTrainingPanel.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "PrintBridge", "_GuidedDeviceGuide.cshtml"),
            Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Shared", "_PrintBridgeDeviceTopics.cshtml"),
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

        // Every training step has a title and a body.
        foreach (var step in GuidedTrainingSteps.All)
        foreach (var suffix in new[] { ".Title", ".Body" })
        {
            var key = Wasla.Web.Models.GuidedSetup.GuidedSetupCopy.TrainingStepKey(step) + suffix;
            Assert.True(english.ContainsKey(key), $"{key} is missing.");
        }
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

    private GuidedSetupController Controller(FakeGuidedSetup state, string? userIdClaim = null, FakeDemos? demos = null)
    {
        var coordinator = new GuidedSetupCoordinator(state, new FixedNavigation(Owner), new FakeSetupStatus(), demos ?? new FakeDemos(), new FakePrintBridgeDevices(), new CapturingLogger());
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
            .Where(method => typeof(Task<IActionResult>).IsAssignableFrom(method.ReturnType))
            // Commands only: the one read-only GET (section status) is tested separately.
            .Where(method => method.GetCustomAttribute<HttpGetAttribute>() is null);

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
