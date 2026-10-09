using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
using Wasla.Domain.Enums;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Models.GuidedSetup;
using Wasla.Infrastructure.Security;
using Wasla.Web.Security;

namespace Wasla.UnitTests.GuidedSetup;

public sealed class GuidedSetupCoordinatorTests
{
    private static readonly TenantNavigationPermissions Owner = new(true, true, true, true, true, true, true, true, true, true, true);
    private static readonly TenantNavigationPermissions Manager = new(true, true, true, true, true, false, false, false, false, true);
    private static readonly TenantNavigationPermissions Viewer = new(true, true, false, true, false, false, false, false, false, false);
    private static readonly TenantNavigationPermissions Kitchen = new(false, true, true, true, false, false, false, false, false, false);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly ClaimsPrincipal _principal = new(new ClaimsIdentity("Tenant"));
    private readonly FakeGuidedSetup _state = new();
    private readonly FakeSetupStatus _readiness = new();
    private readonly FakeDemos _demos = new();
    private readonly FakePrintBridgeDevices _devices = new();
    private readonly CapturingLogger _logger = new();

    // Capabilities -------------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Owner, new[] { GuidedSetupSections.PlatformConnections, GuidedSetupSections.PrintBridge, GuidedSetupSections.LiveScreenDemo })]
    [InlineData(UserRole.Manager, new string[0])]
    [InlineData(UserRole.Kitchen, new string[0])]
    [InlineData(UserRole.Cashier, new string[0])]
    [InlineData(UserRole.Viewer, new string[0])]
    public async Task SectionPlan_IsOwnerOnly_FromTheRealPolicies(UserRole role, string[] expected)
    {
        var tenantId = Guid.NewGuid();
        var navigation = new TenantNavigationAuthorizationService(RealPolicies(tenantId));
        var coordinator = new GuidedSetupCoordinator(_state, navigation, _readiness, _demos, _devices, _logger);

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
        Assert.Equal([GuidedSetupSections.PlatformConnections, GuidedSetupSections.PrintBridge, GuidedSetupSections.LiveScreenDemo], card.Sections);
    }

    [Fact]
    public async Task NotStarted_NonOwners_SeeNoCard_AndStateIsNotRead()
    {
        foreach (var role in new[] { Manager, Kitchen, Viewer })
            Assert.Null(await Coordinator(role).GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None));

        Assert.Equal(0, _state.Reads);
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
    public async Task Start_ForAManager_CreatesNoProgress()
    {
        var result = await Coordinator(Manager).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.UnavailableMessageKey, result.ErrorMessageKey);
        Assert.Equal(GuidedSetupStatus.NotStarted, _state.Get(_user).Status);
        Assert.Equal(0, _state.Writes);
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

        // Skipping takes a Setup tenant live; it opens the normal Live Screen.
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
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
    [InlineData(GuidedSetupSections.LiveScreenDemo, "/orders/live-display")]
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

        // An Owner who lost the settings and Print Bridge policies keeps only order training.
        var ordersOnly = Owner with { CanManageTenantSettings = false, CanManagePrintBridgeDevices = false, CanManageDeviceSecurity = false };
        var result = await Coordinator(ordersOnly).ContinueAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
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

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
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
    public async Task OwnerReachesOrderTrainingOnTheLiveScreen_AndNoSetupStepCompletesTheJourney()
    {
        var coordinator = Coordinator(Owner);
        await coordinator.StartAsync(_tenant, _user, _principal, CancellationToken.None);
        await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PlatformConnections, CancellationToken.None);
        var toTraining = await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        var atTheEnd = await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.LiveScreenDemo, CancellationToken.None);
        var resumed = await coordinator.ContinueAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, toTraining.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, atTheEnd.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, resumed.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _state.Completes);
        Assert.Equal(0, _demos.Starts);
    }

    // Setup sections: deferring versus continuing after a successful setup ---------------------

    [Fact]
    public async Task PrintBridge_NotConnected_ShowsTheUnconnectedPanel_FromTheChecklistsReadinessFact()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = false;

        var panel = await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        var status = await Coordinator(Owner).GetSectionStatusAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.False(panel!.IsReady);
        Assert.Equal(new GuidedSetupSectionStatus(true, false), status);
    }

    [Fact]
    public async Task PrintBridge_Connected_IsReady_ByTheSameFactTheOperationalChecklistUses()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = true;

        var panel = await Coordinator(Owner).GetSectionPanelAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        var status = await Coordinator(Owner).GetSectionStatusAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.True(panel!.IsReady);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, panel.NextSectionKey);
        Assert.Equal(new GuidedSetupSectionStatus(true, true), status);
        Assert.Equal(0, _state.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-section")]
    [InlineData(GuidedSetupSections.PlatformConnections)]
    public async Task SectionStatus_ForAnotherSection_IsNotCurrent_AndWritesNothing(string? section)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        var status = await Coordinator(Owner).GetSectionStatusAsync(_tenant, _user, _principal, section, CancellationToken.None);

        Assert.Equal(GuidedSetupSectionStatus.NotCurrent, status);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task ContinueAfterConnecting_MovesToOrderTraining_OnTheLiveScreen()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = true;

        var result = await Coordinator(Owner).ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
        Assert.Null(result.ErrorMessageKey);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedTrainingSteps.Intro, _state.Get(_user).StepKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _demos.Starts);
    }

    [Fact]
    public async Task RepeatedContinuePosts_MoveOnce_AndNeverRegress()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = true;
        var coordinator = Coordinator(Owner);

        var first = await coordinator.ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        var second = await coordinator.ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);
        var late = await coordinator.AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, first.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, second.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, late.RedirectUrl);
        Assert.Null(second.ErrorMessageKey);
        Assert.Equal(1, _state.Writes);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
    }

    [Fact]
    public async Task ContinueWithoutAConnectedDevice_ChangesNothing_AndExplains()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = false;

        var result = await Coordinator(Owner).ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal("/print-bridge/setup", result.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.NotReadyYetMessageKey, result.ErrorMessageKey);
        Assert.Equal(GuidedSetupSections.PrintBridge, _state.Get(_user).SectionKey);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task SetUpLater_StillDefers_WithoutAConnectedDevice()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = false;

        var result = await Coordinator(Owner).AdvanceAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.Skipped)]
    [InlineData(GuidedSetupStatus.Completed)]
    public async Task ContinueAfterTheJourneyEnded_ChangesNothing(GuidedSetupStatus status)
    {
        _state.Set(_user, status, GuidedSetupSections.PrintBridge);
        _readiness.PrintingReady = true;

        await Coordinator(Owner).ContinueFromSectionAsync(_tenant, _user, _principal, GuidedSetupSections.PrintBridge, CancellationToken.None);

        Assert.Equal(status, _state.Get(_user).Status);
        Assert.Equal(0, _state.Writes);
    }

    // Live Screen: entry points -------------------------------------------------------------

    /// <summary>
    /// Before anyone starts, the Live Screen shows no guided setup at all: guided setup is Owner-only, and every role that
    /// may use it also has the Dashboard, where the first-use decision is. (The Live Screen's own first-use variant for
    /// users without the Dashboard was removed because no role could reach it.)
    /// </summary>
    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task LiveScreen_ShowsNoGuidedSetupBeforeItStarts_TheOwnerDecidesOnTheDashboard_FromTheRealPolicies(UserRole role)
    {
        var tenantId = Guid.NewGuid();
        var navigation = new TenantNavigationAuthorizationService(RealPolicies(tenantId));
        var coordinator = new GuidedSetupCoordinator(_state, navigation, _readiness, _demos, _devices, _logger);
        var principal = RolePrincipal(tenantId, role);

        var panel = await coordinator.GetLiveScreenAsync(tenantId, _user, principal, CancellationToken.None);
        var permissions = await navigation.GetPermissionsAsync(principal);

        Assert.Null(panel);
        // Only the Owner may use guided setup, and the Owner always has the Dashboard where the first-use card is.
        Assert.Equal(role == UserRole.Owner, permissions.CanUseGuidedSetup);
        Assert.True(!permissions.CanUseGuidedSetup || permissions.CanViewReports);

        Assert.Equal(0, _state.Writes);
        Assert.Equal(0, _demos.Starts);
    }

    [Fact]
    public async Task LiveScreen_ForAViewer_ReadsNothingAndShowsNothing()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);

        Assert.Null(await Coordinator(Viewer).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Equal(0, _state.Reads);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.Completed)]
    [InlineData(GuidedSetupStatus.Skipped)]
    public async Task LiveScreen_IsSilent_AfterSkipOrCompletion(GuidedSetupStatus status)
    {
        _state.Set(_user, status, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeReady);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.ReadyForPickup, IsOpen: true);

        Assert.Null(await Coordinator(Owner).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));
    }

    [Fact]
    public async Task LiveScreen_ShowsNothing_WhileTheJourneyIsAtASetupSection()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.PrintBridge);

        Assert.Null(await Coordinator(Owner).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));
    }

    [Fact]
    public async Task LiveScreen_FailsClosed_WhenStateOrThePracticeOrderCannotBeRead()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);
        _demos.FailLatest = true;
        Assert.Null(await Coordinator(Owner).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));

        _state.FailReads = true;
        Assert.Null(await Coordinator(Owner).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Equal(2, _logger.Entries.Count(entry => entry.Level == LogLevel.Error));
    }

    // Live Screen: resuming at the authoritative step -----------------------------------------

    public static TheoryData<string, OrderStatus?, bool, string> ResumeCases => new()
    {
        // saved step, latest practice order status, open, expected step
        { GuidedTrainingSteps.Intro, null, false, GuidedTrainingSteps.Intro },
        { GuidedTrainingSteps.Intro, OrderStatus.Delivered, false, GuidedTrainingSteps.Intro },
        { GuidedTrainingSteps.Intro, OrderStatus.Accepted, true, GuidedTrainingSteps.PracticeAccepted },
        { GuidedTrainingSteps.PracticeNew, OrderStatus.ReadyForPickup, true, GuidedTrainingSteps.PracticeReady },
        { GuidedTrainingSteps.PracticeNew, OrderStatus.OnTheWay, true, GuidedTrainingSteps.PracticeOnTheWay },
        { GuidedTrainingSteps.PracticeReady, OrderStatus.Delivered, false, GuidedTrainingSteps.PracticeDelivered },
        { GuidedTrainingSteps.PracticePreparing, OrderStatus.Preparing, false, GuidedTrainingSteps.Intro },
        { GuidedTrainingSteps.PracticeNew, null, false, GuidedTrainingSteps.Intro }
    };

    [Theory]
    [MemberData(nameof(ResumeCases))]
    public async Task Reload_ResumesAtTheStepThePracticeOrderIsActuallyAt(string saved, OrderStatus? status, bool open, string expected)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, saved);
        _demos.Latest = status is { } s ? new GuidedDemoSummary(Guid.NewGuid(), s, open) : null;

        var panel = await Coordinator(Owner).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.NotNull(panel);
        Assert.Equal(expected, panel.StepKey);
        Assert.Equal((int)Wasla.Application.Orders.LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes, panel.DeliveredWindowMinutes);
        // Reading the page never writes or starts anything.
        Assert.Equal(0, _state.Writes);
        Assert.Equal(0, _demos.Starts);
    }

    // Live Screen: practice order and completion ------------------------------------------------

    [Fact]
    public async Task StartPractice_IsIdempotent_AndSavesTheFirstPracticeStep()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);
        var coordinator = Coordinator(Owner);

        var first = await coordinator.StartPracticeAsync(_tenant, _user, _principal, CancellationToken.None);
        var second = await coordinator.StartPracticeAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, first.RedirectUrl);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, second.RedirectUrl);
        Assert.Null(first.ErrorMessageKey);
        Assert.Equal(1, _demos.Created);
        Assert.Equal(GuidedTrainingSteps.PracticeNew, _state.Get(_user).StepKey);
        Assert.Equal(1, _state.Writes);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.NotStarted, null)]
    [InlineData(GuidedSetupStatus.Skipped, null)]
    [InlineData(GuidedSetupStatus.Completed, null)]
    [InlineData(GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections)]
    public async Task StartPractice_OutsideOrderTraining_StartsNothing(GuidedSetupStatus status, string? section)
    {
        if (status != GuidedSetupStatus.NotStarted)
            _state.Set(_user, status, section);

        var result = await Coordinator(Owner).StartPracticeAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
        Assert.Equal(0, _demos.Starts);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task StartPractice_ForAViewer_StartsNothing()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);

        await Coordinator(Viewer).StartPracticeAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(0, _demos.Starts);
        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task StartPractice_Failure_KeepsTheStep_AndShowsALocalizedError()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);
        _demos.FailStart = true;

        var result = await Coordinator(Owner).StartPracticeAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.PracticeFailedMessageKey, result.ErrorMessageKey);
        Assert.Equal(GuidedTrainingSteps.Intro, _state.Get(_user).StepKey);
        Assert.Equal(0, _state.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OrderStatus.ReadyForPickup)]
    [InlineData(OrderStatus.OnTheWay)]
    public async Task Complete_BeforeTheDeliveredExplanation_IsRefused(OrderStatus? status)
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeReady);
        _demos.Latest = status is { } s ? new GuidedDemoSummary(Guid.NewGuid(), s, IsOpen: true) : null;

        var result = await Coordinator(Owner).CompleteTrainingAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.NotYetDeliveredMessageKey, result.ErrorMessageKey);
        Assert.Equal(GuidedSetupStatus.InProgress, _state.Get(_user).Status);
        Assert.Equal(0, _state.Completes);
    }

    [Fact]
    public async Task Complete_CannotUseADeliveredDemoFromBeforeThePracticeStarted()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.Intro);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false);

        var result = await Coordinator(Owner).CompleteTrainingAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.NotYetDeliveredMessageKey, result.ErrorMessageKey);
        Assert.Equal(0, _state.Completes);
    }

    [Fact]
    public async Task Complete_AfterDelivery_RecordsCompletedOnce_AndShowsNoMoreGuidance()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeReady);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false);
        var coordinator = Coordinator(Owner);

        var first = await coordinator.CompleteTrainingAsync(_tenant, _user, _principal, CancellationToken.None);
        var again = await coordinator.CompleteTrainingAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.TrainingCompletedMessageKey, first.SuccessMessageKey);
        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, first.RedirectUrl);
        Assert.Null(again.SuccessMessageKey);
        Assert.Equal(GuidedSetupStatus.Completed, _state.Get(_user).Status);
        Assert.Equal(1, _state.Completes);
        Assert.Equal(1, _demos.Finishes);
        // The delivered practice order is left alone: it leaves the Live Screen after the delivered window.
        Assert.Equal(OrderStatus.Delivered, _demos.Latest!.Status);
        Assert.Null(await coordinator.GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));
        Assert.Null(await coordinator.GetDashboardCardAsync(_tenant, _user, _principal, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_StillSucceeds_WhenThePracticeOrderCannotBeClosed()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeReady);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false);
        _demos.FailFinish = true;

        var result = await Coordinator(Owner).CompleteTrainingAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.TrainingCompletedMessageKey, result.SuccessMessageKey);
        Assert.Equal(GuidedSetupStatus.Completed, _state.Get(_user).Status);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.Skipped)]
    [InlineData(GuidedSetupStatus.NotStarted)]
    public async Task Complete_NeverOverridesSkipOrSkipsTheJourney(GuidedSetupStatus status)
    {
        if (status != GuidedSetupStatus.NotStarted)
            _state.Set(_user, status, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeDelivered);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.Delivered, IsOpen: false);

        await Coordinator(Owner).CompleteTrainingAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(status, _state.Get(_user).Status);
        Assert.Equal(0, _state.Completes);
    }

    [Fact]
    public async Task EndDuringTraining_RecordsSkipped_AndClosesThePracticeOrder()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeNew);
        _demos.Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.New, IsOpen: true);

        await Coordinator(Owner).EndAsync(_tenant, _user, _principal, confirmed: true, CancellationToken.None);

        Assert.Equal(GuidedSetupStatus.Skipped, _state.Get(_user).Status);
        Assert.False(_demos.Latest!.IsOpen);
        Assert.Null(await Coordinator(Owner).GetLiveScreenAsync(_tenant, _user, _principal, CancellationToken.None));
    }

    [Fact]
    public async Task AnOwnerWithOnlyOrderManagement_StartsAtTheLiveScreenTrainingSection()
    {
        var ordersOnly = Owner with { CanManageTenantSettings = false, CanManagePrintBridgeDevices = false, CanManageDeviceSecurity = false };
        var result = await Coordinator(ordersOnly).StartAsync(_tenant, _user, _principal, CancellationToken.None);

        Assert.Equal(GuidedSetupCoordinator.LiveScreenUrl, result.RedirectUrl);
        Assert.Equal(GuidedSetupSections.LiveScreenDemo, _state.Get(_user).SectionKey);
        Assert.Equal(GuidedTrainingSteps.Intro, _state.Get(_user).StepKey);
        Assert.Equal(0, _demos.Starts);
    }

    // Progress recorded after restaurant actions -----------------------------------------------

    [Fact]
    public async Task PracticeProgress_OnlyMovesForward()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo, GuidedTrainingSteps.PracticeNew);
        var coordinator = Coordinator(Owner);

        await coordinator.RecordPracticeProgressAsync(_tenant, _user, _principal, OrderStatus.Preparing, CancellationToken.None);
        await coordinator.RecordPracticeProgressAsync(_tenant, _user, _principal, OrderStatus.Accepted, CancellationToken.None);
        await coordinator.RecordPracticeProgressAsync(_tenant, _user, _principal, OrderStatus.Preparing, CancellationToken.None);

        Assert.Equal(GuidedTrainingSteps.PracticePreparing, _state.Get(_user).StepKey);
        Assert.Equal(1, _state.Writes);
    }

    [Theory]
    [InlineData(GuidedSetupStatus.Skipped, GuidedSetupSections.LiveScreenDemo)]
    [InlineData(GuidedSetupStatus.Completed, GuidedSetupSections.LiveScreenDemo)]
    [InlineData(GuidedSetupStatus.InProgress, GuidedSetupSections.PlatformConnections)]
    public async Task PracticeProgress_OutsideOrderTraining_WritesNothing(GuidedSetupStatus status, string section)
    {
        _state.Set(_user, status, section);

        await Coordinator(Owner).RecordPracticeProgressAsync(_tenant, _user, _principal, OrderStatus.Accepted, CancellationToken.None);

        Assert.Equal(0, _state.Writes);
    }

    [Fact]
    public async Task PracticeProgress_Failure_IsLoggedAndNeverThrows()
    {
        _state.Set(_user, GuidedSetupStatus.InProgress, GuidedSetupSections.LiveScreenDemo);
        _state.FailWrites = true;

        await Coordinator(Owner).RecordPracticeProgressAsync(_tenant, _user, _principal, OrderStatus.Accepted, CancellationToken.None);

        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void OrderTraining_LivesOnTheLiveScreen()
    {
        Assert.Equal("/orders/live-display", GuidedSetupCoordinator.UrlFor(GuidedSetupSections.LiveScreenDemo, "/dashboard"));
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
        new(_state, new FixedNavigation(permissions), _readiness, _demos, _devices, _logger);

    internal static IAuthorizationService RealPolicies(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            // Mirrors the Web Program.cs registration (see docs/product/roles-and-permissions.md).
            Add(options, TenantPolicies.TenantOwner, UserRole.Owner);
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

    /// <summary>
    /// An in-memory guided-setup store that applies the real Phase 1 transition table. Like the real service it records
    /// when a user started, and when <see cref="Tenant"/> is set, completing or skipping takes that tenant live.
    /// </summary>
    internal sealed class FakeGuidedSetup : IGuidedSetupService
    {
        private readonly Dictionary<Guid, GuidedSetupState> _states = new();

        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Completes { get; private set; }

        /// <summary>The start time recorded for started users.</summary>
        public DateTime StartTime { get; set; } = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>The tenant whose mode a completion or skip moves from Setup to Live, as the real service does.</summary>
        public FakeTenantModes? Tenant { get; set; }

        public GuidedSetupState Get(Guid userId) =>
            _states.TryGetValue(userId, out var state) ? state : GuidedSetupState.NotStarted;

        public void Set(Guid userId, GuidedSetupStatus status, string? section, string? step = null) =>
            _states[userId] = new GuidedSetupState(
                status, section, section is null ? null : step ?? "intro",
                status is GuidedSetupStatus.InProgress or GuidedSetupStatus.Completed ? StartTime : null,
                null, null, null);

        public Task<GuidedSetupState> GetAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Reads++;
            if (FailReads)
                throw new InvalidOperationException("database unavailable");
            return Task.FromResult(Get(userId));
        }

        public Task<GuidedSetupResult> StartAsync(Guid tenantId, Guid userId, GuidedSetupPosition position, CancellationToken ct) =>
            Apply(userId, GuidedSetupCommand.Start, current => current with
            {
                Status = GuidedSetupStatus.InProgress, SectionKey = position.SectionKey, StepKey = position.StepKey,
                StartedAtUtc = current.StartedAtUtc ?? StartTime
            });

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
            if (command is GuidedSetupCommand.Skip or GuidedSetupCommand.Complete)
                Tenant?.Activate();
            return Task.FromResult(new GuidedSetupResult(outcome, next));
        }
    }

    /// <summary>The tenant's operational mode, Live unless a test says otherwise.</summary>
    internal sealed class FakeTenantModes : ITenantOperationalModeService
    {
        public TenantOperationalMode Mode { get; set; } = TenantOperationalMode.Live;
        public bool FailReads { get; set; }
        public List<Guid> ReadTenants { get; } = new();

        /// <summary>The default settings (sync on, no automation) in <see cref="Mode"/>.</summary>
        public Task<TenantAutomationStatus> GetAutomationStatusAsync(Guid tenantId, CancellationToken ct)
        {
            ReadTenants.Add(tenantId);
            if (FailReads)
                throw new InvalidOperationException("database unavailable");
            return Task.FromResult(TenantAutomationStatus.WithoutSettings with { Mode = Mode });
        }

        /// <summary>What completing or skipping does to the tenant: Setup → Live, never back.</summary>
        public void Activate()
        {
            if (Mode == TenantOperationalMode.Setup)
                Mode = TenantOperationalMode.Live;
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

    /// <summary>
    /// One practice order per user, like the real service: Start returns the open one or opens a new one.
    /// Guided setup never moves a practice order itself, so ApplyActionAsync throws.
    /// </summary>
    internal sealed class FakeDemos : IGuidedDemoService
    {
        public bool FailFinish { get; set; }
        public bool FailStart { get; set; }
        public bool FailLatest { get; set; }
        public int Finishes { get; private set; }
        public int Starts { get; private set; }
        public int Created { get; private set; }

        /// <summary>The user's most recent practice order; tests set it to any lifecycle point.</summary>
        public GuidedDemoSummary? Latest { get; set; }

        public Task FinishActiveAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Finishes++;
            if (FailFinish)
                throw new InvalidOperationException("demo store unavailable");
            if (Latest is { IsOpen: true })
                Latest = Latest with { IsOpen = false };
            return Task.CompletedTask;
        }

        public Task<GuidedDemoSessionState> StartAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            Starts++;
            if (FailStart)
                throw new InvalidOperationException("demo store unavailable");
            if (Latest is not { IsOpen: true })
            {
                Created++;
                Latest = new GuidedDemoSummary(Guid.NewGuid(), OrderStatus.New, IsOpen: true);
            }

            return Task.FromResult(new GuidedDemoSessionState(
                Latest.Id, userId, "lokanta", Latest.Status, DateTime.UtcNow, "Demo.CustomerName", null, []));
        }

        public Task<GuidedDemoSummary?> GetLatestAsync(Guid tenantId, Guid userId, CancellationToken ct)
        {
            if (FailLatest)
                throw new InvalidOperationException("demo store unavailable");
            return Task.FromResult(Latest);
        }

        public Task<GuidedDemoSessionState?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            Task.FromResult<GuidedDemoSessionState?>(null);

        /// <summary>The practice order the Live Screen shows; none unless a test sets it.</summary>
        public GuidedDemoSessionState? ForLiveScreen { get; set; }

        public Task<GuidedDemoSessionState?> GetForLiveScreenAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
            Task.FromResult(ForLiveScreen);

        public Task<GuidedDemoActionResult> ApplyActionAsync(Guid tenantId, Guid userId, Guid sessionId, string action, CancellationToken ct) =>
            throw new InvalidOperationException("Guided setup must not act on demos.");
    }

    /// <summary>
    /// Print Bridge devices per tenant, for the device guide's page selection. Only the tenant-scoped list is
    /// readable; every action that changes a device or issues a token fails the test.
    /// </summary>
    internal sealed class FakePrintBridgeDevices : IPrintBridgeDeviceManagementService
    {
        private readonly Dictionary<Guid, List<PrintBridgeDeviceSummaryDto>> _byTenant = new();

        public List<Guid> ListedTenants { get; } = new();
        public bool FailReads { get; set; }

        public PrintBridgeDeviceSummaryDto Add(Guid tenantId, string name, bool isActive = true, DateTime? lastSeenAtUtc = null)
        {
            var device = new PrintBridgeDeviceSummaryDto(
                Guid.NewGuid(), name, isActive, lastSeenAtUtc, "PC", null, null, null,
                PrintBridgeConnectionStatusCalculator.Calculate(isActive, lastSeenAtUtc));
            if (!_byTenant.TryGetValue(tenantId, out var devices))
                _byTenant[tenantId] = devices = new List<PrintBridgeDeviceSummaryDto>();
            devices.Add(device);
            return device;
        }

        public Task<IReadOnlyList<PrintBridgeDeviceSummaryDto>> ListDevicesAsync(Guid customerId, CancellationToken ct)
        {
            ListedTenants.Add(customerId);
            if (FailReads)
                throw new InvalidOperationException("database unavailable");
            return Task.FromResult<IReadOnlyList<PrintBridgeDeviceSummaryDto>>(
                _byTenant.TryGetValue(customerId, out var devices) ? devices.ToArray() : []);
        }

        /// <summary>Tenant-scoped like the real query: another tenant's device id finds nothing.</summary>
        public Task<PrintBridgeDeviceDetailsDto?> GetDeviceDetailsAsync(Guid customerId, Guid deviceId, CancellationToken ct)
        {
            var device = _byTenant.TryGetValue(customerId, out var devices) ? devices.FirstOrDefault(d => d.Id == deviceId) : null;
            return Task.FromResult(device is null
                ? null
                : new PrintBridgeDeviceDetailsDto(device.Id, device.Name, device.IsActive, DateTime.UtcNow, device.LastSeenAtUtc,
                    device.MachineName, null, null, null, device.ConnectionStatus, HasToken: true));
        }

        public Task<PrintBridgeDeviceQuotaDto> GetDeviceQuotaAsync(Guid customerId, CancellationToken ct) =>
            Task.FromResult(new PrintBridgeDeviceQuotaDto(PrintBridgeDeviceLimits.AllowedActiveDeviceCount, 0, true, false));

        public Task<GeneratePrintBridgeTokenResult> CreateDeviceAsync(Guid customerId, string deviceName, CancellationToken ct) => throw Forbidden();
        public Task<GeneratePrintBridgeTokenResult> RegenerateTokenAsync(Guid customerId, Guid deviceId, CancellationToken ct) => throw Forbidden();
        public Task<bool> SetDeviceActiveAsync(Guid customerId, Guid deviceId, bool isActive, CancellationToken ct) => throw Forbidden();
        public Task<RemovePrintBridgeDeviceResult> RemoveDeviceAsync(Guid customerId, Guid deviceId, CancellationToken ct) => throw Forbidden();
        public Task<RenamePrintBridgeDeviceResult> UpdateDeviceNameAsync(Guid customerId, Guid deviceId, string deviceName, CancellationToken ct) => throw Forbidden();

        private static Exception Forbidden([System.Runtime.CompilerServices.CallerMemberName] string? action = null) =>
            new InvalidOperationException($"Guided setup must not call {action}.");
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
