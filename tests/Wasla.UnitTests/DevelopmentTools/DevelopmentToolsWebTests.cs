using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.DevelopmentTools;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.DevelopmentTools;
using Wasla.Web.Models.Help;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.DevelopmentTools;

/// <summary>
/// The temporary Development tenant reset tool: unreachable outside Development or when disabled, Owner only,
/// antiforgery-protected, scoped to the authenticated tenant, and confirmed by typing the exact slug.
/// </summary>
public sealed partial class DevelopmentToolsWebTests
{
    private static readonly string[] Cultures = ["", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU"];
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);
    private readonly Guid _tenantId = Guid.NewGuid();

    // Availability ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Testing", true, false)]
    public void TenantReset_IsAvailableOnlyInDevelopmentWhenExplicitlyEnabled(string environment, bool enabled, bool expected)
    {
        Assert.Equal(expected, DevelopmentToolsAvailability.IsTenantResetAvailable(
            new TestHostEnvironment(environment), new DevelopmentToolsOptions { EnableTenantReset = enabled }));
    }

    [Fact]
    public void TenantReset_IsOffByDefault()
    {
        Assert.False(new DevelopmentToolsOptions().EnableTenantReset);
        Assert.False(DevelopmentToolsAvailability.IsTenantResetAvailable(new TestHostEnvironment("Development"), null));
    }

    [Fact]
    public void Configuration_EnablesTheToolOnlyForLocalDevelopment()
    {
        static bool? Enabled(string file)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", file)));
            return json.RootElement.TryGetProperty("DevelopmentTools", out var tools)
                   && tools.TryGetProperty("EnableTenantReset", out var enabled)
                ? enabled.GetBoolean()
                : null;
        }

        Assert.True(Enabled("appsettings.Development.json"));
        Assert.NotEqual(true, Enabled("appsettings.json"));
        Assert.NotEqual(true, Enabled("appsettings.Production.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Convention_RemovesTheControllerWhenTheToolIsUnavailable(bool available)
    {
        var application = new ApplicationModel();
        var tools = new ControllerModel(typeof(DevelopmentToolsController).GetTypeInfo(),
            typeof(DevelopmentToolsController).GetCustomAttributes(inherit: true));
        var help = new ControllerModel(typeof(HelpController).GetTypeInfo(),
            typeof(HelpController).GetCustomAttributes(inherit: true));
        application.Controllers.Add(tools);
        application.Controllers.Add(help);

        new DevelopmentToolsConvention(available).Apply(application);

        Assert.Equal(available, application.Controllers.Contains(tools));
        Assert.Contains(help, application.Controllers);
    }

    /// <summary>
    /// MVC's own action discovery: when the tool is unavailable the reset action is not registered at all, so
    /// no route reaches it and every request gets 404 before authentication or antiforgery run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MvcActionDiscovery_RegistersTheResetRouteOnlyWhenAvailable(bool available)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var diagnostics = new System.Diagnostics.DiagnosticListener("Wasla.Tests");
        services.AddSingleton(diagnostics);
        services.AddSingleton<System.Diagnostics.DiagnosticSource>(diagnostics);
        services.AddSingleton<IWebHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddControllers(options => options.Conventions.Add(new DevelopmentToolsConvention(available)))
            .AddApplicationPart(typeof(DevelopmentToolsController).Assembly);
        using var provider = services.BuildServiceProvider();

        var actions = provider.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()
            .ToArray();
        var reset = actions.Where(action => action.ControllerTypeInfo == typeof(DevelopmentToolsController)).ToArray();

        Assert.Contains(actions, action => action.ControllerTypeInfo == typeof(HelpController));
        Assert.Equal(available, reset.Length > 0);
        Assert.DoesNotContain(actions, action => action.ControllerTypeInfo != typeof(DevelopmentToolsController)
                                                 && action.AttributeRouteInfo?.Template?.Contains("development-tools", StringComparison.Ordinal) == true);
        if (available)
            Assert.Equal("development-tools/tenant-reset", Assert.Single(reset).AttributeRouteInfo!.Template);
    }

    [Fact]
    public void Startup_RegistersTheConventionFromEnvironmentAndOption()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", "Program.cs"));

        Assert.Contains("DevelopmentToolsAvailability.IsTenantResetAvailable(\n    builder.Environment,", program.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("options.Conventions.Add(new DevelopmentToolsConvention(tenantResetAvailable))", program, StringComparison.Ordinal);
        Assert.Contains("AddTenantRolePolicy(TenantPolicies.TenantOwner, UserRole.Owner)", program, StringComparison.Ordinal);
    }

    // Controller -------------------------------------------------------------------------------

    [Fact]
    public void ResetEndpoint_IsAnOwnerOnlyAntiforgeryPost_ThatTakesNoTenantOrUserFromTheRequest()
    {
        var type = typeof(DevelopmentToolsController);
        Assert.NotNull(type.GetCustomAttribute<DevelopmentTenantResetToolAttribute>());
        var authorize = type.GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Equal(TenantPolicies.TenantOwner, authorize.Policy);
        Assert.Equal(AuthSchemes.Tenant, authorize.AuthenticationSchemes);

        var action = type.GetMethod(nameof(DevelopmentToolsController.ResetTenant))!;
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
        Assert.Null(action.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Null(type.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.Equal(new[] { "confirmSlug", "ct" }, action.GetParameters().Select(p => p.Name));
    }

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Kitchen, false)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Viewer, false)]
    public async Task OwnerPolicy_AdmitsOnlyTheOwner(UserRole role, bool expected)
    {
        var authorization = OwnerPolicy(_tenantId);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("TenantId", _tenantId.ToString()), new Claim(ClaimTypes.Role, role.ToString())], "Tenant"));

        Assert.Equal(expected, (await authorization.AuthorizeAsync(principal, TenantPolicies.TenantOwner)).Succeeded);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task Reset_Returns404_WhenTheToolIsUnavailable(string environment, bool enabled)
    {
        var reset = new RecordingReset();
        var controller = Controller(reset, environment, enabled);

        Assert.IsType<NotFoundResult>(await controller.ResetTenant("reset-me", CancellationToken.None));
        Assert.Empty(reset.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Reset-Me")]
    [InlineData(" reset-me")]
    [InlineData("reset-me ")]
    [InlineData("another-tenant")]
    public async Task Reset_RefusesAConfirmationThatIsNotExactlyTheSlug(string? typed)
    {
        var reset = new RecordingReset();
        var controller = Controller(reset);

        var result = await controller.ResetTenant(typed, CancellationToken.None);

        Assert.StartsWith("/help", Assert.IsType<LocalRedirectResult>(result).Url, StringComparison.Ordinal);
        Assert.Equal("DevelopmentTools.Reset.SlugMismatch", controller.TempData["Error"]);
        Assert.Empty(reset.Calls);
    }

    [Fact]
    public async Task Reset_WithTheExactSlug_ResetsOnlyTheAuthenticatedTenant_AndLandsOnTheDashboard()
    {
        var reset = new RecordingReset();
        var controller = Controller(reset);

        var result = await controller.ResetTenant("reset-me", CancellationToken.None);

        Assert.Equal("/dashboard", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal(new[] { _tenantId }, reset.Calls);
        Assert.Equal("DevelopmentTools.Reset.Succeeded", controller.TempData["Success"]);
        Assert.Equal(DevelopmentToolsController.ResetToastDurationMs, controller.TempData["SuccessDurationMs"]);
        var keys = JsonSerializer.Deserialize<string[]>((string)controller.TempData["ClearBrowserKeys"]!)!;
        Assert.Equal(new[] { $"Wasla.setupWelcome.dismissed.{_tenantId}" }, keys);
    }

    [Fact]
    public async Task Reset_Failure_ShowsAnError_AndClearsNothing()
    {
        var reset = new RecordingReset { Succeeds = false };
        var controller = Controller(reset);

        var result = await controller.ResetTenant("reset-me", CancellationToken.None);

        Assert.StartsWith("/help", Assert.IsType<LocalRedirectResult>(result).Url, StringComparison.Ordinal);
        Assert.Equal("DevelopmentTools.Reset.Failed", controller.TempData["Error"]);
        Assert.False(controller.TempData.ContainsKey("ClearBrowserKeys"));
        Assert.False(controller.TempData.ContainsKey("Success"));
    }

    [Fact]
    public async Task Reset_PartlyApplied_SaysSo_AndAsksForARerun()
    {
        var reset = new RecordingReset { Succeeds = false, Partial = true };
        var controller = Controller(reset);

        await controller.ResetTenant("reset-me", CancellationToken.None);

        Assert.Equal("DevelopmentTools.Reset.Partial", controller.TempData["Error"]);
        Assert.False(controller.TempData.ContainsKey("ClearBrowserKeys"));
    }

    // Help page --------------------------------------------------------------------------------

    [Theory]
    [InlineData("Development", true, true, true)]
    [InlineData("Development", true, false, false)]
    [InlineData("Development", false, true, false)]
    [InlineData("Production", true, true, false)]
    [InlineData("Staging", true, true, false)]
    public async Task HelpPage_OffersTheToolOnlyToAnOwnerInEnabledDevelopment(string environment, bool enabled, bool isOwner, bool shown)
    {
        var controller = new HelpController(
            new FixedNavigation(Owner),
            new FixedPolicy(isOwner),
            new FixedTenant(_tenantId),
            new TestHostEnvironment(environment),
            Options.Create(new DevelopmentToolsOptions { EnableTenantReset = enabled }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var model = Assert.IsType<HelpPageViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);

        Assert.Equal(shown, model.ShowDevelopmentTools);
        Assert.Equal(shown ? "tenant" : null, model.DevelopmentTenantResetSlug);
    }

    [Fact]
    public void HelpView_RendersTheToolOnlyWhenAllowed_InASeparateSection()
    {
        var help = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml");
        var tool = Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "_DevelopmentTools.cshtml");

        Assert.Matches(new Regex(@"@if \(Model\.ShowDevelopmentTools\)\s*\{\s*@await Html\.PartialAsync\(""_DevelopmentTools"", Model\.DevelopmentTenantResetSlug\)\s*\}"), help);
        Assert.DoesNotContain("development-tools/tenant-reset", help, StringComparison.Ordinal);
        Assert.Contains("id=\"help-development-tools\"", tool, StringComparison.Ordinal);
        Assert.Contains("method=\"post\" action=\"/development-tools/tenant-reset\" data-guided-setup-form", tool, StringComparison.Ordinal);
        Assert.Contains("@Html.AntiForgeryToken()", tool, StringComparison.Ordinal);
        Assert.Contains("name=\"confirmSlug\"", tool, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"waslaTenantResetTitle\" aria-describedby=\"waslaTenantResetBody\"", tool, StringComparison.Ordinal);
        Assert.Contains("@L[\"DevelopmentTools.Reset.BrowserPermission\"]", tool, StringComparison.Ordinal);
        Assert.Contains("@L[\"DevelopmentTools.Reset.StopWorker\"]", tool, StringComparison.Ordinal);
        // No tenant or user identifiers are posted; only the typed slug.
        Assert.DoesNotContain("name=\"tenantId\"", tool, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name=\"userId\"", tool, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResetCopy_WarnsAboutWhatIsRemoved_InTurkish_AndEveryCultureHasIt()
    {
        var turkish = Load(".tr-TR");
        Assert.Equal("Test tenant’ını sıfırla", turkish["DevelopmentTools.Reset.Button"]);
        var warning = turkish["DevelopmentTools.Reset.Warning"];
        foreach (var part in new[] { "siparişleri", "platform bağlantıları", "Print Bridge cihazları", "test ayarları" })
            Assert.Contains(part, warning, StringComparison.Ordinal);
        Assert.Contains("Chrome", turkish["DevelopmentTools.Reset.BrowserPermission"], StringComparison.Ordinal);

        var english = Load(".en-US");
        var keys = english.Keys.Where(k => k.StartsWith("DevelopmentTools.", StringComparison.Ordinal)).ToArray();
        Assert.True(keys.Length >= 15);
        foreach (var culture in Cultures)
        {
            var resources = Load(culture);
            foreach (var key in keys)
            {
                Assert.True(resources.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value), $"{key} missing in {culture}");
                Assert.Equal(Placeholders(english[key]), Placeholders(value!));
            }
        }

        var used = new[]
            {
                Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "_DevelopmentTools.cshtml"),
                Read("src", "Wasla.Web", "Areas", "Tenant", "Views", "Help", "Index.cshtml"),
                Read("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "DevelopmentToolsController.cs")
            }
            .SelectMany(source => Regex.Matches(source, "\"(DevelopmentTools\\.[A-Za-z.]+)\"").Select(m => m.Groups[1].Value))
            .Distinct();
        foreach (var key in used)
            Assert.True(english.ContainsKey(key), $"{key} is used but not defined.");
    }

    [Fact]
    public void BrowserCleanup_RemovesOnlyListedWaslaKeys_AndNeverClearsStorage()
    {
        var notifications = Read("src", "Wasla.Web", "Views", "Shared", "_Notifications.cshtml");
        Assert.Contains("key.StartsWith(\"Wasla.\", StringComparison.Ordinal)", notifications, StringComparison.Ordinal);
        Assert.Contains("window.localStorage.removeItem(key)", notifications, StringComparison.Ordinal);

        var web = Path.Combine(Root(), "src", "Wasla.Web");
        var sources = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".cshtml", StringComparison.Ordinal))
                           && !path.Contains(Path.DirectorySeparatorChar + "lib" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            Assert.False(text.Contains("localStorage.clear(", StringComparison.Ordinal), path);
            Assert.False(text.Contains("sessionStorage.clear(", StringComparison.Ordinal), path);
        }
    }

    // ------------------------------------------------------------------------------------------

    private DevelopmentToolsController Controller(RecordingReset reset, string environment = "Development", bool enabled = true)
    {
        var httpContext = new DefaultHttpContext();
        return new DevelopmentToolsController(
            new SluggedTenant(_tenantId, "reset-me"),
            reset,
            new TestHostEnvironment(environment),
            Options.Create(new DevelopmentToolsOptions { EnableTenantReset = enabled }),
            new KeyLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };
    }

    private static IAuthorizationService OwnerPolicy(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Mirrors Program.cs: AddTenantRolePolicy(TenantPolicies.TenantOwner, UserRole.Owner).
        services.AddAuthorization(options => options.AddPolicy(TenantPolicies.TenantOwner, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new TenantRoleRequirement(UserRole.Owner));
        }));
        services.AddSingleton<ICurrentTenantService>(new FixedTenant(tenantId));
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> Load(string culture) =>
        XDocument.Load(Path.Combine(Root(), "src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!.Elements("data")
            .Where(e => e.Attribute("name") is not null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    private static string Read(params string[] segments) => File.ReadAllText(Path.Combine([Root(), .. segments]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class RecordingReset : ITenantDevelopmentResetService
    {
        public bool Succeeds { get; init; } = true;
        public bool Partial { get; init; }
        public List<Guid> Calls { get; } = new();

        public Task<TenantDevelopmentResetResult> ResetAsync(Guid tenantId, CancellationToken ct)
        {
            Calls.Add(tenantId);
            return Task.FromResult(Succeeds
                ? new TenantDevelopmentResetResult(true)
                : Partial ? TenantDevelopmentResetResult.PrintBridgeOnly : TenantDevelopmentResetResult.Failed);
        }
    }

    private sealed class SluggedTenant(Guid id, string slug) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } = new(id, "Reset Me", slug, slug + ".wasla.local");
    }

    private sealed class FixedPolicy(bool succeeds) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(succeeds && policyName == TenantPolicies.TenantOwner ? AuthorizationResult.Success() : AuthorizationResult.Failed());
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

/// <summary>A host environment with a chosen name; nothing else is used by the code under test.</summary>
internal sealed class TestHostEnvironment(string environmentName) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Wasla.Web";
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
