using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Security;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

public sealed class TenantSetupPanelTests
{
    private static readonly string[] Cultures = ["tr-TR", "en-US", "ar-SA", "ru-RU"];
    private static readonly string[] ResourceKeys =
    [
        "Setup.Title",
        "Setup.Progress",
        "Setup.ProgressCaption",
        "Setup.ReadyTitle",
        "Setup.ReadyBody",
        "Setup.OpenLiveScreen",
        "Setup.AddTeamMember",
        "Setup.FinishSetup",
        "Setup.GuidanceCompleted",
        "Setup.NextStepBody",
        "Setup.Optional",
        "Setup.WelcomeTitle",
        "Setup.WelcomeTitleRest",
        "Setup.WelcomeBody",
        "Setup.StartSetup",
        "Setup.BrowsePanel",
        "Setup.Restaurant.Title",
        "Setup.Platform.Title",
        "Setup.Platform.Action",
        "Setup.Platform.ConnectedMany",
        "Setup.Notifications.Title",
        "Setup.Notifications.Hint",
        "Setup.Printing.Title",
        "Setup.Printing.Complete",
        "Setup.LiveScreen.Title",
        "Setup.LiveScreen.Action"
    ];

    private readonly Guid _tenantId = Guid.NewGuid();

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Kitchen, false)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Viewer, false)]
    public async Task SetupChecklist_IsVisibleOnlyToOwner(UserRole role, bool expected)
    {
        var authorization = BuildAuthorizationService(_tenantId);

        var result = await authorization.AuthorizeAsync(
            Principal(_tenantId, role),
            resource: null,
            TenantPolicies.CanManageTenantSettings);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public void Dashboard_RendersSetupOnlyWhenViewModelProvidesIt()
    {
        var dashboard = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "Index.cshtml");
        var panel = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Dashboard", "_TenantSetupPanel.cshtml");
        var controller = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "DashboardController.cs");
        var mapper = ReadRepositoryFile("src", "Wasla.Web", "Ui", "TenantSetupPanelMapper.cs");

        Assert.Contains("Model.Setup is not null", dashboard, StringComparison.Ordinal);
        Assert.Contains("TenantPolicies.CanManageTenantSettings", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"complete-setup\")]", controller, StringComparison.Ordinal);
        Assert.Contains("status.IsSetupGuidanceCompleted", controller, StringComparison.Ordinal);
        Assert.Contains("action=\"/dashboard/complete-setup\"", panel, StringComparison.Ordinal);
        Assert.Contains("Model.ShowAddTeamMember", panel, StringComparison.Ordinal);
        Assert.Contains("\"/platform-connections\"", mapper, StringComparison.Ordinal);
        Assert.Contains("\"/settings/orders\"", mapper, StringComparison.Ordinal);
        Assert.Contains("\"/print-bridge/setup\"", mapper, StringComparison.Ordinal);
        Assert.Contains("\"/settings/account\"", mapper, StringComparison.Ordinal);
        Assert.Contains("href=\"/orders/live-display\"", panel, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", panel, StringComparison.Ordinal);
        Assert.Contains("href=\"/settings/users\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Wasla'yı kullanıma hazırla", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("demo order", panel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignupWelcome_LandsOnDashboardInsteadOfStaticOnboardingCards()
    {
        var auth = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "AuthController.cs");
        var onboarding = ReadRepositoryFile("src", "Wasla.Web", "Areas", "Tenant", "Controllers", "OnboardingController.cs");

        Assert.Contains("return Redirect(\"/dashboard\");", auth, StringComparison.Ordinal);
        Assert.Contains("Redirect(\"/dashboard\")", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("return Redirect(\"/onboarding\");", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupCopy_IsLocalizedForEverySupportedCulture()
    {
        foreach (var culture in Cultures)
        {
            var resources = ReadResourceValues(culture);
            foreach (var key in ResourceKeys)
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{key} missing for {culture}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} empty for {culture}.");
            }
        }

        Assert.Equal("Wasla'yı kullanıma hazırla", ReadResourceValues("tr-TR")["Setup.Title"]);
        Assert.Equal("Review restaurant information", ReadResourceValues("en-US")["Setup.Restaurant.Title"]);
        Assert.Equal("اختياري", ReadResourceValues("ar-SA")["Setup.Optional"]);
        Assert.Equal("Необязательно", ReadResourceValues("ru-RU")["Setup.Optional"]);
    }

    private static IAuthorizationService BuildAuthorizationService(Guid currentTenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(TenantPolicies.CanManageTenantSettings, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new TenantRoleRequirement(UserRole.Owner));
            });
        });
        services.AddSingleton<ICurrentTenantService>(new FixedCurrentTenantService(currentTenantId));
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Principal(Guid tenantId, UserRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim("TenantId", tenantId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            authenticationType: "Tenant"));

    private static Dictionary<string, string> ReadResourceValues(string culture)
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Wasla.Web",
            "Resources",
            $"SharedResource.{culture}.resx");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { GetRepositoryRoot() }.Concat(segments).ToArray()));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class FixedCurrentTenantService : ICurrentTenantService
    {
        public FixedCurrentTenantService(Guid tenantId)
        {
            CurrentTenant = new ResolvedTenantDto(tenantId, "Tenant", "tenant", "tenant.wasla.local");
        }

        public ResolvedTenantDto? CurrentTenant { get; }
    }
}
