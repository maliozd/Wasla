using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.GuidedSetup;
using Wasla.Web.Security;

namespace Wasla.UnitTests.GuidedSetup;

public sealed class GuidedSetupCoordinatorTests
{
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true);
    private static readonly TenantNavigationPermissions Manager = new(true, true, true, true, true, false, false, false, false, true);
    private static readonly TenantNavigationPermissions Viewer = new(true, true, false, true, false, false, false, false, false, false);
    private static readonly TenantNavigationPermissions Kitchen = new(false, true, true, true, false, false, false, false, false, false);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly ClaimsPrincipal _principal = new(new ClaimsIdentity("Tenant"));
    private readonly FakeGuidedSetup _state = new();
    private readonly FakeSetupStatus _readiness = new();
    private readonly FakeDemos _demos = new();
    private readonly CapturingLogger _logger = new();

    // Capabilities -------------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Owner, new[] { GuidedSetupSections.PlatformConnections, GuidedSetupSections.PrintBridge, GuidedSetupSections.LiveScreenDemo })]
    [InlineData(UserRole.Manager, new[] { GuidedSetupSections.LiveScreenDemo })]
    [InlineData(UserRole.Kitchen, new[] { GuidedSetupSections.LiveScreenDemo })]
    [InlineData(UserRole.Cashier, new[] { GuidedSetupSections.LiveScreenDemo })]
    [InlineData(UserRole.Viewer, new string[0])]
    public async Task SectionPlan_ComesFromTheRealPolicies(UserRole role, string[] expected)
    {
        var tenantId = Guid.NewGuid();
        var navigation = new TenantNavigationAuthorizationService(RealPolicies(tenantId));
        var coordinator = new GuidedSetupCoordinator(_state, navigation, _readiness, _demos, _logger);

        var capabilities = await coordinator.GetCapabilitiesAsync(RolePrincipal(tenantId, role));

        Assert.Equal(expected, GuidedSetupPlan.SectionsFor(capabilities));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void PrintBridgeSection_NeedsBothPrintBridgePolicies(bool devices, bool security)
    {
        var permissions = Owner with { CanManagePrintBridgeDevices = devices, CanManageDeviceSecurity = security };

        var capabilities = GuidedSetupCoordinator.ToCapabilities(permissions);

        Assert.False(capabilities.CanManagePrintBridge);
        Assert.DoesNotContain(GuidedSetupSections.PrintBridge, GuidedSetupPlan.SectionsFor(capabilities));
    }

    [Fact]
    public void PlatformSection_FollowsTheTenantSettingsPolicy()
    {
        Assert.True(GuidedSetupCoordinator.ToCapabilities(Owner).CanManagePlatformConnections);
        Assert.False(GuidedSetupCoordinator.ToCapabilities(Owner with { CanManageTenantSettings = false }).CanManagePlatformConnections);
    }

    // Dashboard card -------------------------------------------------------------------------

    [Fact]
    public async Task NotStarted_EligibleUser_SeesTheFirstUseDecision()
    {
        var card = await Coordinator(Owner).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.NotNull(card);
        Assert.Equal(GuidedSetupCardKind.FirstUse, card.Kind);
        Assert.Equal(3, card.Sections.Count);
        Assert.False(card.IsOrderTrainingOnly);
    }

    [Fact]
    public async Task NotStarted_OrderManager_SeesOnlyOrderTraining()
    {
        var card = await Coordinator(Manager).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.NotNull(card);
        Assert.True(card.IsOrderTrainingOnly);
    }

    [Fact]
    public async Task UserWithoutApplicableSections_SeesNoPrompt_AndStateIsNotRead()
    {
        var card = await Coordinator(Viewer).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Null(card);
        Assert.Equal(0, _state.Reads);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.Completed)]
    [InlineData(GuidedSetupStatus.Skipped)]
    public async Task FinishedUsers_SeeNoGuidedSetupUi(GuidedSetupStatus status)
    {
        _state.Set(_user, status, GuidedSetupSections.PrintBridge);

        Assert.Null(await Coordinator(Owner).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Null(await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None));
        Assert.Null(await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None));
    }

    [Fact]
    public async Task StateReadFailure_LogsAnError_AndHidesGuidance()
    {
        _state.FailReads = true;

        var card = await Coordinator(Owner).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None);
        var panel = await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);

        Assert.Null(card);
        Assert.Null(panel);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InProgress_ShowsTheResumeCardAtTheSavedSection()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        var card = await Coordinator(Owner).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCardKind.Resume, card!.Kind);
        Assert.Equal(GuidedSetupSections.PrintBridge, card.CurrentSectionKey);
    }

    [Fact]
    public async Task InProgress_AtOrderTraining_ShowsItIsNext_WithoutStartingAnything()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);

        var card = await Coordinator(Owner).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCardKind.OrderTrainingNext, card!.Kind);
        Assert.Equal(0, _demos.Starts);
        Assert.Equal(0, _state.Writes);
    }

    // Start -------------------------------------------------------------------------------

    [Fact]
    public async Task Start_UsesTheFirstPermittedSection_AndGoesToItsPage()
    {
        var result = await Coordinator(Owner).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/platform-connections", result.RedirectUrl);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(GuidedSetupSections.PlatformConnections, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupCoordinator.IntroStep, _state.Get(_user).StepKey);
    }

    [Fact]
    public async Task Start_ForAnOrderManager_SavesOrderTraining_AndHandsOffToTheDashboard()
    {
        var result = await Coordinator(Manager).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/dashboard", result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _demos.Starts);
    }

    [Fact]
    public async Task Start_WithoutApplicableSections_CreatesNoProgress()
    {
        var result = await Coordinator(Viewer).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.UnavailableMessageKey, result.ErrorMessageKey);
        Assert.Equal(GuidedSetupStatus.NotStarted, _state.Get(_user).Status);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task StartingAgain_KeepsTheSavedSection()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        var result = await Coordinator(Owner).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/print-bridge/setup", result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);
    }

    [Fact]
    public async Task Start_AfterSkipping_ChangesNothing()
    {
        _state.Set(_user, GuidedSetupStatus.Skipped, null);

        var result = await Coordinator(Owner).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/dashboard", result.RedirectUrl);
        Assert.Equal(GuidedSetupStatus.Skipped, _state.Get(_user).Status);
    }

    // Skip and End ------------------------------------------------------------------------

    [Fact]
    public async Task InitialSkip_RecordsSkipped_NeverCompleted_AndFinishesAnOpenDemo()
    {
        var result = await Coordinator(Owner).SkipAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/dashboard", result.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.ClosedMessageKey, result.SuccessMessageKey);
        Assert.Equal(GuidedSetupStatus.Skipped, _state.Get(_user).Status);
        Assert.Equal(0, _state.Completes);
        Assert.Equal(1, _demos.Finishes);
    }

    [Fact]
    public async Task InitialSkip_DoesNotEndAStartedJourney()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);

        await Coordinator(Owner).SkipAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _demos.Finishes);
    }

    [Fact]
    public async Task Skip_StillSucceeds_WhenTheDemoCannotBeFinished()
    {
        _demos.FailFinish = true;

        var result = await Coordinator(Owner).SkipAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupStatus.Skipped, _state.Get(_user).Status);
        Assert.Null(result.ErrorMessageKey);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task End_WithoutConfirmation_ChangesNothing()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        await Coordinator(Owner).EndAsync(_tenant, _user, _principal, confirmed: false, CancellationToken.None);

        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task ConfirmedEnd_RecordsSkipped_AndFinishesAnOpenDemo()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);

        var result = await Coordinator(Owner).EndAsync(_tenant, _user, _principal, confirmed: true, CancellationToken.None);

        Assert.Equal(GuidedSetupStatus.Skipped, _state.Get(_user).Status);
        Assert.Equal(GuidedSetupCoordinator.ClosedMessageKey, result.SuccessMessageKey);
        Assert.Equal(1, _demos.Finishes);
        Assert.Equal(0, _state.Completes);
    }

    [Fact]
    public async Task CommandFailure_IsLogged_AndReturnsALocalizedError()
    {
        _state.FailWrites = true;

        var result = await Coordinator(Owner).SkipAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.FailedMessageKey, result.ErrorMessageKey);
        Assert.Equal("/dashboard", result.RedirectUrl);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    // Continue ----------------------------------------------------------------------------

    [Theory]
    [InlineData(GuidedSetupSections.PlatformConnections, "/platform-connections")]
    [InlineData(GuidedSetupSections.PrintBridge, "/print-bridge/setup")]
    [InlineData(GuidedSetupSections.LiveScreenDemo, "/dashboard")]
    public async Task Continue_GoesToTheSavedAuthorizedSection(string section, string expectedUrl)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, section);

        var result = await Coordinator(Owner).ContinueAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(expectedUrl, result.RedirectUrl);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task Continue_AfterPermissionsWereRevoked_AdvancesToTheNextPermittedSection()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);

        var result = await Coordinator(Manager).ContinueAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/dashboard", result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
    }

    [Fact]
    public void ResolveSection_SkipsRevokedSections_InJourneyOrder()
    {
        string[] withoutPrintBridge = [GuidedSetupSections.PlatformConnections, GuidedSetupSections.LiveScreenDemo];

        Assert.Equal(GuidedSetupSections.LiveScreenDemo, GuidedSetupCoordinator.ResolveSection(GuidedSetupSections.PrintBridge, withoutPrintBridge));
        Assert.Equal(GuidedSetupSections.PlatformConnections, GuidedSetupCoordinator.ResolveSection(GuidedSetupSections.PlatformConnections, withoutPrintBridge));
        Assert.Equal(GuidedSetupSections.PlatformConnections, GuidedSetupCoordinator.ResolveSection(null, withoutPrintBridge));
        Assert.Null(GuidedSetupCoordinator.ResolveSection(GuidedSetupSections.PrintBridge, []));
    }

    // Sections ----------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlatformPanel_ReflectsActualConnectionReadiness(bool ready)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);
        _readiness.PlatformReady = ready;

        var panel = await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);

        Assert.Equal(ready, panel!.IsReady);
        Assert.Equal(1, panel.SectionNumber);
        Assert.Equal(3, panel.SectionCount);
        Assert.Equal(GuidedSetupSections.PrintBridge, panel.NextSectionKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrintBridgePanel_ReflectsActualDeviceReadiness(bool ready)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = ready;

        var panel = await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(ready, panel!.IsReady);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, panel.NextSectionKey);
    }

    [Fact]
    public async Task Panel_AppearsOnlyOnTheCurrentSectionsPage()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);

        Assert.Null(await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None));
    }

    [Fact]
    public async Task UserMissingAPrintBridgePolicy_NeverGetsThePrintBridgePanel()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        var devicesOnly = Owner with { CanManageDeviceSecurity = false };

        Assert.Null(await Coordinator(devicesOnly).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeavingPlatformConnections_ReadyOrLater_MovesToPrintBridge_AndMarksNothingSetUp(bool ready)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);
        _readiness.PlatformReady = ready;

        var result = await Coordinator(Owner).AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);

        Assert.Equal("/print-bridge/setup", result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);
        Assert.Equal(ready, _readiness.PlatformReady);
        Assert.Equal(0, _readiness.Writes);
    }

    [Fact]
    public async Task LeavingPrintBridge_MovesToOrderTraining_WithoutCompletingOrStartingADemo()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        var result = await Coordinator(Owner).AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal("/dashboard", result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupCoordinator.IntroStep, _state.Get(_user).StepKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _demos.Starts);
    }

    [Fact]
    public async Task OwnerWithoutPrintBridgePolicies_GoesFromPlatformsStraightToOrderTraining()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections);
        var noPrintBridge = Owner with { CanManagePrintBridgeDevices = false };

        await Coordinator(noPrintBridge).AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);

        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
    }

    [Fact]
    public async Task AdvancingFromAStalePage_ChangesNothing()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        var result = await Coordinator(Owner).AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);

        Assert.Equal("/print-bridge/setup", result.RedirectUrl);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task OrderTraining_IsTheLastPhaseTwoStop_AndNoPathCompletesTheJourney()
    {
        var coordinator = Coordinator(Owner);
        await coordinator.StartAsync(_tenant, _user, _principal, CancellationToken.None);
        await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);
        await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        var atTheEnd = await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.LiveScreenDemo, CancellationToken.None);
        await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal("/dashboard", atTheEnd.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _state.Completes);
    }

    [Fact]
    public void LegacyTourSuppressionHooks_AreGone_BecauseNoPageMountsALegacyTour()
    {
        Assert.Null(typeof(IGuidedSetupCoordinator).GetMethod("IsInProgressAsync"));
        Assert.Null(typeof(GuidedSetupCoordinator).GetMethod("IsInProgressAsync"));
        Assert.Null(typeof(Wasla.Web.Models.PlatformConnections.PlatformConnectionListViewModel).GetProperty("GuidedSetupInProgress"));
    }

    [Fact]
    public void Coordinator_NeverTouchesTheLegacyTourTable()
    {
        var parameters = typeof(GuidedSetupCoordinator).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(Wasla.Application.Abstractions.Tours.IUserProductTourService));
    }

    private GuidedSetupCoordinator Coordinator(TenantNavigationPermissions permissions) =>
        new(_state, new FixedNavigation(permissions), _readiness, _demos, _logger);

    private static IAuthorizationService RealPolicies(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            // Mirrors the Web Program.cs registration (see docs/product/roles-and-permissions.md).
            Add(options, TenantPolicies.TenantManagerOrOwner, UserRole.Owner, UserRole.Manager);
            Add(options, TenantPolicies.CanManageTenantUsers, UserRole.Owner);
            Add(options, TenantPolicies.CanManageTenantSettings, UserRole.Owner);
            Add(options, TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
            Add(options, TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
            Add(options, TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            Add(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            Add(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
            Add(options, TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            Add(options, TenantPolicies.CanViewReports, UserRole.Owner, UserRole.Manager, UserRole.Viewer);
        });
        services.AddSingleton<ICurrentTenantService>(new FixedTenant(tenantId));
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        static void Add(AuthorizationOptions options, string name, params UserRole[] roles) =>
            options.AddPolicy(name, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new TenantRoleRequirement(roles));
            });
    }

    private static ClaimsPrincipal RolePrincipal(Guid tenantId, UserRole role) =>
        new(new ClaimsIdentity(
            [new Claim("TenantId", tenantId.ToString()), new Claim(ClaimTypes.Role, role.ToString())],
            authenticationType: "Tenant"));

    internal sealed class FixedTenant(Guid tenantId) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } = new(tenantId, "Tenant", "tenant", "tenant.wasla.local");
    }

    internal sealed class FixedNavigation(TenantNavigationPermissions permissions) : ITenantNavigationAuthorizationService
    {
        public Task<TenantNavigationPermissions> GetPermissionsAsync(ClaimsPrincipal user) => Task.FromResult(permissions);
    }

    /// <summary>An in-memory guided-setup store that applies the real Phase 1 transition table.</summary>
    internal sealed class FakeGuidedSetup : IGuidedSetupService
    {
        private readonly Dictionary<Guid, GuidedSetupState> _states = new();

        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Completes { get; private set; }

        public GuidedSetupState Get(Guid userId) =>
            _states.TryGetValue(userId, out var state) ? state : GuidedSetupState.NotStarted;

        public void Set(Guid userId, GuidedSetupStatus status, string? section) =>
            _states[userId] = new GuidedSetupState(status, section, section is null ? null : "intro", null, null, null, null);

        public Task<GuidedSetupState> GetAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Reads++;
            if (FailReads)
                throw new InvalidOperationException("database unavailable");
            return Task.FromResult(Get(userId));
        }

        public Task<GuidedSetupResult> StartAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct) =>
            Apply(userId, GuidedSetupCommand.Start, current => current with { Status = GuidedSetupStatus.InProgress, SectionKey = position.SectionKey, StepKey = position.StepKey });

        public Task<GuidedSetupResult> SaveProgressAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct) =>
            Apply(userId, GuidedSetupCommand.SaveProgress, current => current with { SectionKey = position.SectionKey, StepKey = position.StepKey });

        public Task<GuidedSetupResult> SkipAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            Apply(userId, GuidedSetupCommand.Skip, current => current with { Status = GuidedSetupStatus.Skipped });

        public Task<GuidedSetupResult> CompleteAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Completes++;
            return Apply(userId, GuidedSetupCommand.Complete, current => current with { Status = GuidedSetupStatus.Completed });
        }

        private Task<GuidedSetupResult> Apply(Guid userId, GuidedSetupCommand command, Func<GuidedSetupState, GuidedSetupState> change)
        {
            if (FailWrites)
                throw new InvalidOperationException("database unavailable");
            var current = Get(userId);
            var outcome = GuidedSetupTransitions.Evaluate(command, current.Status);
            if (outcome != GuidedSetupOutcome.Applied)
                return Task.FromResult(new GuidedSetupResult(outcome, current));
            Writes++;
            var next = change(current);
            _states[userId] = next;
            return Task.FromResult(new GuidedSetupResult(outcome, next));
        }
    }

    internal sealed class FakeSetupStatus : ITenantSetupStatusService
    {
        public bool PlatformReady { get; set; }
        public bool PrintingReady { get; set; }
        public int Writes { get; private set; }

        public Task<TenantSetupStatus> GetAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            Task.FromResult(new TenantSetupStatus(
                IsReady: false,
                RequiredStepsCompleted: 0,
                RequiredStepsTotal: 3,
                ActivePlatforms: [],
                Restaurant: Step(TenantSetupStepKind.RestaurantProfile, false),
                Platform: Step(TenantSetupStepKind.PlatformConnection, PlatformReady),
                Notifications: Step(TenantSetupStepKind.Notifications, false),
                Printing: Step(TenantSetupStepKind.Printing, PrintingReady),
                LiveScreen: Step(TenantSetupStepKind.LiveScreen, false)));

        public Task<bool> CompleteGuidanceAsync(Guid tenantId, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult(true);
        }

        private static TenantSetupStep Step(TenantSetupStepKind kind, bool complete) =>
            new(kind, complete, IsOptional: false, CountsTowardReadiness: true, IsAvailable: true);
    }

    internal sealed class FakeDemos : IGuidedDemoService
    {
        public bool FailFinish { get; set; }
        public int Finishes { get; private set; }
        public int Starts { get; private set; }

        public Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Finishes++;
            if (FailFinish)
                throw new InvalidOperationException("demo store unavailable");
            return Task.CompletedTask;
        }

        public Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Starts++;
            throw new InvalidOperationException("Guided setup must not start a demo in this phase.");
        }

        public Task<GuidedDemoSessionState?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            Task.FromResult<GuidedDemoSessionState?>(null);

        public Task<GuidedDemoSessionState?> GetForLiveScreenAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            Task.FromResult<GuidedDemoSessionState?>(null);

        public Task<GuidedDemoActionResult> ApplyActionAsync(Guid tenantId, Guid userId, Guid sessionId, string action, CancellationToken ct) =>
            throw new InvalidOperationException("Guided setup must not act on demos.");
    }

    internal sealed class CapturingLogger : ILogger<GuidedSetupCoordinator>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
