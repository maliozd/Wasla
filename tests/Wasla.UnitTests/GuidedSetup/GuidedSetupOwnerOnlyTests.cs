using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.GuidedSetup;
using Wasla.Infrastructure.Security;
using Wasla.Web.Security;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.GuidedSetup;

/// <summary>
/// Guided setup and order training are Owner-only, enforced on the server. The endpoints are checked the way the
/// authorization middleware decides: each action's [Authorize] metadata combined into one policy and evaluated with
/// the policy table registered in Web Program.cs (read from that file, so it cannot drift from the app). The
/// coordinator is then driven with the real guided-setup service to show a refused role writes nothing and can never
/// take a Setup tenant live, while Help stays open to every role.
/// </summary>
public sealed class GuidedSetupOwnerOnlyTests : IDisposable
{
    private static readonly UserRole[] NonOwners = [UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer];

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly OperationalModeTestClock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every guided-setup and practice-order endpoint; a new one must be added here (and so reviewed).</summary>
    public static TheoryData<string, string> GuidedEndpoints => new()
    {
        { nameof(GuidedSetupController), nameof(GuidedSetupController.Start) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.Skip) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.End) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.Continue) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.Advance) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.ContinueFromSection) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.SectionStatus) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.PrintBridgeDevice) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.StartPractice) },
        { nameof(GuidedSetupController), nameof(GuidedSetupController.CompleteTraining) },
        { nameof(GuidedDemoController), nameof(GuidedDemoController.Start) },
        { nameof(GuidedDemoController), nameof(GuidedDemoController.Act) },
        { nameof(GuidedDemoController), nameof(GuidedDemoController.Finish) }
    };

    [Fact]
    public void TheEndpointListCoversEveryActionOfBothControllers()
    {
        var listed = GuidedEndpoints.Select(row => $"{row.Data.Item1}.{row.Data.Item2}").Order(StringComparer.Ordinal);
        var actual = new[] { typeof(GuidedSetupController), typeof(GuidedDemoController) }
            .SelectMany(type => Actions(type).Select(method => $"{type.Name}.{method.Name}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(listed, actual);
    }

    [Theory]
    [MemberData(nameof(GuidedEndpoints))]
    public async Task OnlyTheOwnerPassesTheEndpointsAuthorization(string controller, string action)
    {
        var policy = await EndpointPolicyAsync(Controller(controller), action);

        Assert.True((await Authorization(_tenant).AuthorizeAsync(Principal(_tenant, UserRole.Owner), null, policy)).Succeeded);
        foreach (var role in NonOwners)
            Assert.False((await Authorization(_tenant).AuthorizeAsync(Principal(_tenant, role), null, policy)).Succeeded, role.ToString());
        // An Owner of another tenant is refused as well; nothing about this tenant is revealed.
        Assert.False((await Authorization(_tenant).AuthorizeAsync(Principal(Guid.NewGuid(), UserRole.Owner), null, policy)).Succeeded);
    }

    [Fact]
    public async Task HelpStaysOpenToEveryRole()
    {
        var policy = await EndpointPolicyAsync(typeof(HelpController), nameof(HelpController.Index));

        foreach (var role in NonOwners.Append(UserRole.Owner))
            Assert.True((await Authorization(_tenant).AuthorizeAsync(Principal(_tenant, role), null, policy)).Succeeded, role.ToString());
    }

    [Fact]
    public async Task TheGuidedSetupPermission_IsTheOwnerPolicy()
    {
        var navigation = new TenantNavigationAuthorizationService(Authorization(_tenant));

        Assert.True((await navigation.GetPermissionsAsync(Principal(_tenant, UserRole.Owner))).CanUseGuidedSetup);
        foreach (var role in NonOwners)
            Assert.False((await navigation.GetPermissionsAsync(Principal(_tenant, role))).CanUseGuidedSetup, role.ToString());
    }

    [Fact]
    public async Task TheOwner_SeesTheFirstUseCard_AndCanStart_WhileTheTenantStaysInSetup()
    {
        await NewTenantAsync();
        var owner = Principal(_tenant, UserRole.Owner);

        var card = await Coordinator().GetDashboardCardAsync(_tenant, _user, owner, Ct);
        Assert.Equal(GuidedSetupCardKind.FirstUse, card!.Kind);

        var started = await Coordinator().StartAsync(_tenant, _user, owner, Ct);
        Assert.Equal("/platform-connections", started.RedirectUrl);
        Assert.Equal(GuidedSetupStatus.InProgress, (await GuidedSetup().GetAsync(_tenant, _user, Ct)).Status);
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task ANonOwner_SeesNoCardPanelOrTraining(UserRole role)
    {
        await NewTenantAsync();
        var principal = Principal(_tenant, role);

        Assert.Null(await Coordinator().GetDashboardCardAsync(_tenant, _user, principal, Ct));
        foreach (var section in GuidedSetupSections.All)
            Assert.Null(await Coordinator().GetSectionPanelAsync(_tenant, _user, principal, section, Ct));
        Assert.Null(await Coordinator().GetLiveScreenAsync(_tenant, _user, principal, Ct));
        Assert.Null(await Coordinator().GetLiveScreenIsolationAsync(_tenant, _user, principal, Ct));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task EveryCommandFromANonOwner_WritesNothing_AndNeverTakesTheTenantLive(UserRole role)
    {
        await NewTenantAsync();
        var principal = Principal(_tenant, role);

        await RunEveryCommandAsync(principal);

        Assert.Equal(GuidedSetupStatus.NotStarted, (await GuidedSetup().GetAsync(_tenant, _user, Ct)).Status);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(0, await db.UserGuidedSetupStates.CountAsync(Ct));
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    public async Task ANonOwnersEarlierTrainingRow_IsNeitherChangedNorFinished(UserRole role)
    {
        // Started before training became Owner-only.
        await NewTenantAsync();
        await GuidedSetup().StartAsync(_tenant, _user, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeNew), Ct);
        var before = await GuidedSetup().GetAsync(_tenant, _user, Ct);
        _clock.Now = _clock.Now.AddMinutes(5);

        await RunEveryCommandAsync(Principal(_tenant, role));

        Assert.Equal(before, await GuidedSetup().GetAsync(_tenant, _user, Ct));
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));
    }

    /// <summary>
    /// The Dashboard's only first-use presentation: from the real policies the Owner's card always covers the whole
    /// journey (never order training alone), and Skip records the choice, takes the tenant live and removes the card.
    /// </summary>
    [Fact]
    public async Task TheOwnersFirstUseCard_CoversTheWholeJourney_AndSkipEndsIt()
    {
        await NewTenantAsync();
        var owner = Principal(_tenant, UserRole.Owner);

        var card = await Coordinator().GetDashboardCardAsync(_tenant, _user, owner, Ct);
        Assert.Equal(GuidedSetupCardKind.FirstUse, card!.Kind);
        Assert.Equal([GuidedSetupSections.PlatformConnections, GuidedSetupSections.PrintBridge, GuidedSetupSections.LiveScreenDemo], card.Sections);

        await Coordinator().SkipAsync(_tenant, _user, owner, Ct);

        Assert.Equal(GuidedSetupStatus.Skipped, (await GuidedSetup().GetAsync(_tenant, _user, Ct)).Status);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
        Assert.Null(await Coordinator().GetDashboardCardAsync(_tenant, _user, owner, Ct));
        Assert.Null(await Coordinator().GetLiveScreenAsync(_tenant, _user, owner, Ct));
    }

    [Fact]
    public async Task OnlyTheOwnersSkip_TakesASetupTenantLive()
    {
        await NewTenantAsync();
        var manager = Guid.NewGuid();
        await _tenants.SeedUserAsync(_tenant, manager, UserRole.Manager);

        await Coordinator().SkipAsync(_tenant, manager, Principal(_tenant, UserRole.Manager), Ct);
        Assert.Equal(TenantOperationalMode.Setup, await _tenants.ModeAsync(_tenant));

        await Coordinator().SkipAsync(_tenant, _user, Principal(_tenant, UserRole.Owner), Ct);
        Assert.Equal(TenantOperationalMode.Live, await _tenants.ModeAsync(_tenant));
    }

    public void Dispose() => _tenants.Dispose();

    private async Task RunEveryCommandAsync(ClaimsPrincipal principal)
    {
        var coordinator = Coordinator();
        await coordinator.StartAsync(_tenant, _user, principal, Ct);
        await coordinator.SkipAsync(_tenant, _user, principal, Ct);
        await coordinator.EndAsync(_tenant, _user, principal, confirmed: true, Ct);
        await coordinator.ContinueAsync(_tenant, _user, principal, Ct);
        foreach (var section in GuidedSetupSections.All)
        {
            await coordinator.AdvanceAsync(_tenant, _user, principal, section, Ct);
            await coordinator.ContinueFromSectionAsync(_tenant, _user, principal, section, Ct);
        }
        await coordinator.StartPracticeAsync(_tenant, _user, principal, Ct);
        await coordinator.CompleteTrainingAsync(_tenant, _user, principal, Ct);
        await coordinator.RecordPracticeProgressAsync(_tenant, _user, principal, OrderStatus.Accepted, Ct);
    }

    private async Task NewTenantAsync()
    {
        await using (var db = await _tenants.CreateAsync(_tenant, CancellationToken.None))
            await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(db, _clock.UtcNow, CancellationToken.None);
        await _tenants.SeedUserAsync(_tenant, _user);
    }

    private GuidedSetupService GuidedSetup() => new(_tenants, _clock);

    private GuidedSetupCoordinator Coordinator()
    {
        // A delivered practice order, so a Complete would be allowed if the user were permitted at all.
        var demos = new FakeDemos { Latest = new Wasla.Application.Demos.GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false) };
        return new GuidedSetupCoordinator(GuidedSetup(), new TenantNavigationAuthorizationService(Authorization(_tenant)),
            new FakeSetupStatus { PlatformReady = true, PrintingReady = true }, demos, new FakePrintBridgeDevices(), new CapturingLogger());
    }

    private static Type Controller(string name) =>
        name == nameof(GuidedSetupController) ? typeof(GuidedSetupController) : typeof(GuidedDemoController);

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.GetCustomAttributes<NonActionAttribute>().Count() == 0);

    /// <summary>As the authorization middleware does: the controller's and the action's [Authorize] data, combined.</summary>
    internal static async Task<AuthorizationPolicy> EndpointPolicyAsync(Type controller, string action)
    {
        var method = Actions(controller).Single(m => m.Name == action);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        var data = controller.GetCustomAttributes<AuthorizeAttribute>().Concat(method.GetCustomAttributes<AuthorizeAttribute>()).ToArray();
        var provider = new DefaultAuthorizationPolicyProvider(Options.Create(ProgramPolicyOptions()));
        return (await AuthorizationPolicy.CombineAsync(provider, data))!;
    }

    internal static IAuthorizationService Authorization(Guid currentTenant)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => Register(options));
        services.AddSingleton<ICurrentTenantService>(new FixedTenant(currentTenant));
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static AuthorizationOptions ProgramPolicyOptions()
    {
        var options = new AuthorizationOptions();
        Register(options);
        return options;
    }

    /// <summary>The role policies exactly as Web Program.cs registers them (the shared tenant policy table).</summary>
    private static void Register(AuthorizationOptions options)
    {
        var program = File.ReadAllText(Path.Combine(Root(), "src", "Wasla.Web", "Program.cs"));
        Assert.Contains("options.AddWaslaTenantRolePolicies()", program, StringComparison.Ordinal);
        options.AddWaslaTenantRolePolicies();

        Assert.Equal([UserRole.Owner], Wasla.Application.Security.WaslaTenantPolicies.AllowedRoles[TenantPolicies.TenantOwner]);
    }

    private ClaimsPrincipal Principal(Guid tenantId, UserRole role) =>
        new(new ClaimsIdentity(
            [new Claim("TenantId", tenantId.ToString()), new Claim(ClaimTypes.Role, role.ToString()), new Claim(ClaimTypes.NameIdentifier, _user.ToString())],
            authenticationType: "Tenant"));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
