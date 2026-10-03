using System.Security.Claims;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;
using Wasla.Web.Models.GuidedSetup;
using Wasla.Web.Security;

namespace Wasla.Web.GuidedSetup;

/// <summary>
/// Web orchestration for guided setup: which sections the current user may see (from the
/// existing authorization policies), where each section lives, and what to show. State
/// transitions stay in <see cref="IGuidedSetupService"/>; nothing here decides them.
/// </summary>
public interface IGuidedSetupCoordinator
{
    Task<GuidedSetupCapabilities> GetCapabilitiesAsync(ClaimsPrincipal user);

    /// <summary>The Dashboard card, or null when guided setup has nothing to show (or cannot be read).</summary>
    Task<GuidedSetupDashboardViewModel?> GetDashboardCardAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>The page panel for <paramref name="sectionKey"/>, only while it is the user's current section.</summary>
    Task<GuidedSetupSectionPanelViewModel?> GetSectionPanelAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, string sectionKey, CancellationToken ct);

    Task<GuidedSetupCommandResult> StartAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// The first-use Skip. It never ends a journey that has started; that needs <see cref="EndAsync"/>. Skipping
    /// takes a tenant that is still in Setup live; like every guided-setup command it is Owner-only (an empty plan for
    /// anyone else), and it opens the Live Screen.
    /// </summary>
    Task<GuidedSetupCommandResult> SkipAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// Ends a started journey permanently (a Skip, so it also takes a Setup tenant live and opens the Live Screen).
    /// Requires the user's explicit confirmation.
    /// </summary>
    Task<GuidedSetupCommandResult> EndAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, bool confirmed, CancellationToken ct);

    Task<GuidedSetupCommandResult> ContinueAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>Defers <paramref name="fromSectionKey"/> ("I'll set it up later") and moves to the next permitted section.</summary>
    Task<GuidedSetupCommandResult> AdvanceAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, string? fromSectionKey, CancellationToken ct);

    /// <summary>
    /// Continues after <paramref name="fromSectionKey"/> was set up successfully. Moves on only when the
    /// section is ready by the operational checklist's own readiness fact; otherwise changes nothing.
    /// </summary>
    Task<GuidedSetupCommandResult> ContinueFromSectionAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, string? fromSectionKey, CancellationToken ct);

    /// <summary>Read-only: whether <paramref name="sectionKey"/> is the user's current section and whether it is ready.</summary>
    Task<GuidedSetupSectionStatus> GetSectionStatusAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, string? sectionKey, CancellationToken ct);

    /// <summary>
    /// The device guide shown on the Print Bridge device pages, or null when it does not apply: only while guided
    /// setup is in progress, Print Bridge is the user's current section (which needs both Print Bridge policies)
    /// and Print Bridge is connected by the checklist's own readiness fact. Read-only.
    /// </summary>
    Task<GuidedDeviceGuideViewModel?> GetDeviceGuideAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// Read-only: the page where the user's guided setup currently continues (the user's home page when there is
    /// none). A connected Print Bridge section continues on its device guide. Nothing is written.
    /// </summary>
    Task<string> GetCurrentSectionUrlAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// The Live Screen's guidance, or null when there is none (or it cannot be read). Read-only:
    /// the step is derived from the saved position and the practice order, and nothing is written.
    /// </summary>
    Task<GuidedTrainingViewModel?> GetLiveScreenAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// Whether this user's Live Screen keeps real orders out of their order training, and since when they have been
    /// in guided setup: whenever the current user (an Owner) is in order training, the condition that shows the
    /// training panel, whatever the tenant's operational mode. It is per user and only changes what this user sees;
    /// automation follows the tenant's mode alone. Read-only. A read failure means no isolation (the normal Live Screen).
    /// </summary>
    Task<GuidedTrainingIsolation?> GetLiveScreenIsolationAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>Starts the practice order, or returns to the one already open. Never creates a second one.</summary>
    Task<GuidedSetupCommandResult> StartPracticeAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>Completes guided setup, only once the practice order was delivered and explained.</summary>
    Task<GuidedSetupCommandResult> CompleteTrainingAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// Saves the training step after the user moved their practice order to <paramref name="status"/>.
    /// Only moves forward, only during order training, and never fails the demo action itself.
    /// </summary>
    Task RecordPracticeProgressAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, OrderStatus status, CancellationToken ct);
}

/// <summary>Where to go after a command, and an optional localized message key.</summary>
public sealed record GuidedSetupCommandResult(string RedirectUrl, string? SuccessMessageKey = null, string? ErrorMessageKey = null);

/// <summary>An isolated trainee's Live Screen: real orders received since <paramref name="TrainingStartedAtUtc"/> are only counted.</summary>
public sealed record GuidedTrainingIsolation(DateTime TrainingStartedAtUtc);

/// <summary>A setup section's live state for its page: whether it is the user's current section, and ready.</summary>
public sealed record GuidedSetupSectionStatus(bool IsCurrent, bool IsReady)
{
    public static GuidedSetupSectionStatus NotCurrent { get; } = new(false, false);
}

public sealed class GuidedSetupCoordinator : IGuidedSetupCoordinator
{
    /// <summary>The first step of every section. Setup sections are one page-level panel, so it is their only step.</summary>
    public const string IntroStep = GuidedTrainingSteps.Intro;

    /// <summary>Order training happens on the Live Screen.</summary>
    public const string LiveScreenUrl = "/orders/live-display";

    /// <summary>The Print Bridge device list; a device's own page is <c>{DevicesUrl}/{id}</c>.</summary>
    public const string DevicesUrl = "/print-bridge/devices";

    /// <summary>
    /// The connected Print Bridge panel's link to the device guide. It resolves the target on the server at
    /// request time (see <see cref="GetCurrentSectionUrlAsync"/>), so no device id ever reaches the browser.
    /// </summary>
    public const string DeviceGuideEntryUrl = "/guided-setup/print-bridge/device";

    public const string ClosedMessageKey = "GuidedSetup.Closed";
    public const string FailedMessageKey = "GuidedSetup.ActionFailed";
    public const string UnavailableMessageKey = "GuidedSetup.Unavailable";
    public const string TrainingCompletedMessageKey = "GuidedSetup.Training.Completed";
    public const string PracticeFailedMessageKey = "GuidedSetup.Training.PracticeFailed";
    public const string NotYetDeliveredMessageKey = "GuidedSetup.Training.NotYetDelivered";
    public const string NotReadyYetMessageKey = "GuidedSetup.Panel.NotReadyYet";

    private readonly IGuidedSetupService _guidedSetup;
    private readonly ITenantNavigationAuthorizationService _navigation;
    private readonly ITenantSetupStatusService _setupStatus;
    private readonly IGuidedDemoService _demos;
    private readonly IPrintBridgeDeviceManagementService _devices;
    private readonly ILogger<GuidedSetupCoordinator> _logger;

    public GuidedSetupCoordinator(
        IGuidedSetupService guidedSetup,
        ITenantNavigationAuthorizationService navigation,
        ITenantSetupStatusService setupStatus,
        IGuidedDemoService demos,
        IPrintBridgeDeviceManagementService devices,
        ILogger<GuidedSetupCoordinator> logger)
    {
        _guidedSetup = guidedSetup;
        _navigation = navigation;
        _setupStatus = setupStatus;
        _demos = demos;
        _devices = devices;
        _logger = logger;
    }

    public async Task<GuidedSetupCapabilities> GetCapabilitiesAsync(ClaimsPrincipal user) =>
        ToCapabilities(await _navigation.GetPermissionsAsync(user).ConfigureAwait(false));

    /// <summary>
    /// Guided setup and order training are Owner-only (<see cref="TenantNavigationPermissions.CanUseGuidedSetup"/>, the
    /// TenantOwner policy that also guards every guided-setup endpoint): anyone else gets no section, so no card, panel,
    /// training or command. Within that, platform guidance follows the policy PlatformConnectionsController enforces,
    /// and the Print Bridge section creates device tokens, so it needs both Print Bridge policies; with only one of
    /// them the user gets no Print Bridge section instead of a partial journey.
    /// </summary>
    public static GuidedSetupCapabilities ToCapabilities(TenantNavigationPermissions permissions) =>
        permissions.CanUseGuidedSetup
            ? new(
                CanManagePlatformConnections: permissions.CanManageTenantSettings,
                CanManagePrintBridge: permissions.CanManagePrintBridgeDevices && permissions.CanManageDeviceSecurity,
                CanManageOrders: permissions.CanManageOrders)
            : new(CanManagePlatformConnections: false, CanManagePrintBridge: false, CanManageOrders: false);

    public async Task<GuidedSetupDashboardViewModel?> GetDashboardCardAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct)
    {
        var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
        var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
        if (plan.Count == 0)
            return null;

        var state = await TryReadAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (state is null)
            return null;

        switch (state.Status)
        {
            case GuidedSetupStatus.NotStarted:
                return new GuidedSetupDashboardViewModel(GuidedSetupCardKind.FirstUse, plan, null);
            case GuidedSetupStatus.InProgress:
                var current = ResolveSection(state.SectionKey, plan);
                var kind = current == GuidedSetupSections.LiveScreenDemo
                    ? GuidedSetupCardKind.OrderTrainingNext
                    : GuidedSetupCardKind.Resume;
                return new GuidedSetupDashboardViewModel(kind, plan, current);
            default:
                return null;
        }
    }

    public async Task<GuidedSetupSectionPanelViewModel?> GetSectionPanelAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, string sectionKey, CancellationToken ct)
    {
        var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
        var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
        if (!plan.Contains(sectionKey, StringComparer.Ordinal))
            return null;

        var state = await TryReadAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (state is not { Status: GuidedSetupStatus.InProgress }
            || ResolveSection(state.SectionKey, plan) != sectionKey)
            return null;

        var position = IndexOf(plan, sectionKey);
        return new GuidedSetupSectionPanelViewModel(
            sectionKey,
            await IsReadyAsync(tenantId, userId, sectionKey, ct).ConfigureAwait(false),
            position + 1,
            plan.Count,
            NextSection(sectionKey, plan));
    }

    public Task<GuidedSetupCommandResult> StartAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "start", async (permissions, home) =>
        {
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            if (plan.Count == 0)
                return new GuidedSetupCommandResult(home, ErrorMessageKey: UnavailableMessageKey);

            var result = await _guidedSetup
                .StartAsync(tenantId, userId, new GuidedSetupPosition(plan[0], IntroStep), ct)
                .ConfigureAwait(false);
            if (result.Outcome == GuidedSetupOutcome.InvalidTransition)
                return new GuidedSetupCommandResult(home);
            if (!result.Succeeded)
                return new GuidedSetupCommandResult(home, ErrorMessageKey: FailedMessageKey);

            // Starting again keeps the saved position, so go wherever the user actually is.
            var current = await EnsurePermittedSectionAsync(tenantId, userId, result.State, plan, ct).ConfigureAwait(false);
            return new GuidedSetupCommandResult(await SectionUrlAsync(tenantId, userId, current, home, ct).ConfigureAwait(false));
        });

    public Task<GuidedSetupCommandResult> SkipAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "skip", async (permissions, home) =>
        {
            // Guided setup is Owner-only (anyone else has no section), and skipping it takes a Setup tenant live.
            if (GuidedSetupPlan.SectionsFor(ToCapabilities(permissions)).Count == 0)
                return new GuidedSetupCommandResult(home);

            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (state.Status != GuidedSetupStatus.NotStarted)
            {
                // A started journey is only ended through the confirmed End action.
                return new GuidedSetupCommandResult(home);
            }

            return await SkipAndCloseAsync(tenantId, userId, permissions, home, ct).ConfigureAwait(false);
        });

    public Task<GuidedSetupCommandResult> EndAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, bool confirmed, CancellationToken ct) =>
        RunAsync(user, "end", async (permissions, home) =>
        {
            if (!confirmed || GuidedSetupPlan.SectionsFor(ToCapabilities(permissions)).Count == 0)
                return new GuidedSetupCommandResult(home);

            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (state.Status != GuidedSetupStatus.InProgress)
                return new GuidedSetupCommandResult(home);

            return await SkipAndCloseAsync(tenantId, userId, permissions, home, ct).ConfigureAwait(false);
        });

    public Task<GuidedSetupCommandResult> ContinueAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "continue", async (permissions, home) =>
        {
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (plan.Count == 0 || state.Status != GuidedSetupStatus.InProgress)
                return new GuidedSetupCommandResult(home);

            var current = await EnsurePermittedSectionAsync(tenantId, userId, state, plan, ct).ConfigureAwait(false);
            return new GuidedSetupCommandResult(await SectionUrlAsync(tenantId, userId, current, home, ct).ConfigureAwait(false));
        });

    public Task<GuidedSetupCommandResult> AdvanceAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, string? fromSectionKey, CancellationToken ct) =>
        RunAsync(user, "advance", (permissions, home) =>
            MoveOnAsync(tenantId, userId, permissions, home, fromSectionKey, requireReady: false, ct));

    public Task<GuidedSetupCommandResult> ContinueFromSectionAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, string? fromSectionKey, CancellationToken ct) =>
        RunAsync(user, "section-continue", (permissions, home) =>
            MoveOnAsync(tenantId, userId, permissions, home, fromSectionKey, requireReady: true, ct));

    public async Task<GuidedSetupSectionStatus> GetSectionStatusAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, string? sectionKey, CancellationToken ct)
    {
        if (sectionKey is null || !GuidedSetupSections.IsKnown(sectionKey))
            return GuidedSetupSectionStatus.NotCurrent;

        var panel = await GetSectionPanelAsync(tenantId, userId, user, sectionKey, ct).ConfigureAwait(false);
        return panel is null ? GuidedSetupSectionStatus.NotCurrent : new GuidedSetupSectionStatus(true, panel.IsReady);
    }

    /// <summary>
    /// Leaves the current setup section for the next permitted one. Deferring ("set it up later") always
    /// moves on and never marks anything as set up; continuing after a successful setup
    /// (<paramref name="requireReady"/>) first re-checks the same readiness fact the operational checklist
    /// uses, so a page that only looked connected cannot skip ahead. A page for a section the user already
    /// left (a repeated post, another tab) changes nothing and shows the real current section.
    /// </summary>
    private async Task<GuidedSetupCommandResult> MoveOnAsync(
        Guid tenantId,
        Guid userId,
        TenantNavigationPermissions permissions,
        string home,
        string? fromSectionKey,
        bool requireReady,
        CancellationToken ct)
    {
        var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
        var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (plan.Count == 0 || state.Status != GuidedSetupStatus.InProgress)
            return new GuidedSetupCommandResult(home);

        var current = ResolveSection(state.SectionKey, plan)!;
        if (!string.Equals(fromSectionKey, current, StringComparison.Ordinal))
        {
            // A stale page (another tab moved on, or a repeated post): show the real current section, change nothing.
            return new GuidedSetupCommandResult(await SectionUrlAsync(tenantId, userId, current, home, ct).ConfigureAwait(false));
        }

        // Not ready: back to the section's setup page (for Print Bridge, never its device guide).
        if (requireReady && !await IsReadyAsync(tenantId, userId, current, ct).ConfigureAwait(false))
            return new GuidedSetupCommandResult(UrlFor(current, home), ErrorMessageKey: NotReadyYetMessageKey);

        var next = NextSection(current, plan);
        if (next is null)
            return new GuidedSetupCommandResult(await SectionUrlAsync(tenantId, userId, current, home, ct).ConfigureAwait(false));

        var saved = await _guidedSetup
            .SaveProgressAsync(tenantId, userId, new GuidedSetupPosition(next, IntroStep), ct)
            .ConfigureAwait(false);
        return saved.Succeeded
            ? new GuidedSetupCommandResult(await SectionUrlAsync(tenantId, userId, next, home, ct).ConfigureAwait(false))
            : new GuidedSetupCommandResult(home, ErrorMessageKey: FailedMessageKey);
    }

    public async Task<GuidedDeviceGuideViewModel?> GetDeviceGuideAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct)
    {
        // The Print Bridge panel exists only for its current section and both Print Bridge policies, and carries
        // the readiness fact; the guide adds nothing to those conditions except that Print Bridge is connected.
        var panel = await GetSectionPanelAsync(tenantId, userId, user, GuidedSetupSections.PrintBridge, ct).ConfigureAwait(false);
        return panel is { IsReady: true }
            ? new GuidedDeviceGuideViewModel(panel.SectionNumber, panel.SectionCount, panel.NextSectionKey)
            : null;
    }

    public async Task<string> GetCurrentSectionUrlAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct)
    {
        var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
        var home = HomeUrlFor(permissions);
        var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
        var state = await TryReadAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (plan.Count == 0 || state is not { Status: GuidedSetupStatus.InProgress })
            return home;

        // ResolveSection only reads; unlike the Continue command, nothing is saved here.
        return await SectionUrlAsync(tenantId, userId, ResolveSection(state.SectionKey, plan), home, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The page for a section right now. Print Bridge has two: its setup page until it is connected, then the device
    /// guide, so a reload or a resume never sends a connected user back to the setup instructions or past the guide.
    /// </summary>
    private async Task<string> SectionUrlAsync(Guid tenantId, Guid userId, string? sectionKey, string home, CancellationToken ct) =>
        sectionKey == GuidedSetupSections.PrintBridge && await IsReadyAsync(tenantId, userId, sectionKey, ct).ConfigureAwait(false)
            ? await DeviceGuidePageAsync(tenantId, ct).ConfigureAwait(false)
            : UrlFor(sectionKey, home);

    /// <summary>
    /// Where the device guide is shown: the details page of the one device of this tenant that is connected right
    /// now (the same 60-second heartbeat fact as readiness), or the device list when that is not exactly one device.
    /// The device comes from the tenant-scoped device query on the server; nothing is taken from the request, and
    /// with several connected devices none is guessed.
    /// </summary>
    private async Task<string> DeviceGuidePageAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            var devices = await _devices.ListDevicesAsync(tenantId, ct).ConfigureAwait(false);
            var connected = devices.Where(device => device.IsActive && device.IsConnected).Take(2).ToArray();
            return connected.Length == 1 ? $"{DevicesUrl}/{connected[0].Id}" : DevicesUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Print Bridge devices could not be read; the device guide opens on the device list: {ExceptionType}", ex.GetType().Name);
            return DevicesUrl;
        }
    }

    public async Task<GuidedTrainingViewModel?> GetLiveScreenAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct)
    {
        var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
        var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
        if (!permissions.CanViewLiveScreen || !plan.Contains(GuidedSetupSections.LiveScreenDemo, StringComparer.Ordinal))
            return null;

        // Before starting, the Owner decides on the Dashboard; the Live Screen only shows order training itself.
        var state = await TryReadAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (state is not { Status: GuidedSetupStatus.InProgress }
            || ResolveSection(state.SectionKey, plan) != GuidedSetupSections.LiveScreenDemo)
            return null;

        var sectionNumber = IndexOf(plan, GuidedSetupSections.LiveScreenDemo) + 1;

        GuidedDemoSummary? latest;
        try
        {
            latest = await _demos.GetLatestAsync(tenantId, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Practice order could not be read; order training is hidden: {ExceptionType}", ex.GetType().Name);
            return null;
        }

        return new GuidedTrainingViewModel(
            GuidedTrainingSteps.Derive(state.StepKey, latest),
            sectionNumber,
            plan.Count,
            DeliveredWindowMinutes);
    }

    public async Task<GuidedTrainingIsolation?> GetLiveScreenIsolationAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            if (!permissions.CanViewLiveScreen || !plan.Contains(GuidedSetupSections.LiveScreenDemo, StringComparer.Ordinal))
                return null;

            // Per user only: the tenant's operational mode plays no part here (it governs automation, not this screen).
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            var isCurrentUserInOrderTraining = IsInOrderTraining(state, plan);
            return isCurrentUserInOrderTraining && state.StartedAtUtc is { } startedAtUtc
                ? new GuidedTrainingIsolation(startedAtUtc)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Order training isolation could not be read; the Live Screen shows every order: {ExceptionType}",
                ex.GetType().Name);
            return null;
        }
    }

    public Task<GuidedSetupCommandResult> StartPracticeAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "practice", LiveScreenUrl, async (permissions, home) =>
        {
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (!IsInOrderTraining(state, plan))
                return new GuidedSetupCommandResult(home);

            GuidedDemoSessionState practice;
            try
            {
                // Returns the open practice order when there is one; the unique open-session index
                // makes concurrent starts return the same order.
                practice = await _demos.StartAsync(tenantId, userId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("Practice order could not be started: {ExceptionType}", ex.GetType().Name);
                return new GuidedSetupCommandResult(home, ErrorMessageKey: PracticeFailedMessageKey);
            }

            await SaveTrainingStepAsync(tenantId, userId, state, GuidedTrainingSteps.ForDemoStatus(practice.Status), ct).ConfigureAwait(false);
            return new GuidedSetupCommandResult(home);
        });

    public Task<GuidedSetupCommandResult> CompleteTrainingAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "complete", LiveScreenUrl, async (permissions, home) =>
        {
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (!IsInOrderTraining(state, plan))
                return new GuidedSetupCommandResult(home);

            // Authoritative: the practice order itself must have been delivered, whatever the page showed.
            var latest = await _demos.GetLatestAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (GuidedTrainingSteps.Derive(state.StepKey, latest) != GuidedTrainingSteps.PracticeDelivered)
                return new GuidedSetupCommandResult(home, ErrorMessageKey: NotYetDeliveredMessageKey);

            var result = await _guidedSetup.CompleteAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (!result.Succeeded)
                return new GuidedSetupCommandResult(home, ErrorMessageKey: FailedMessageKey);
            if (result.Outcome == GuidedSetupOutcome.Unchanged)
                return new GuidedSetupCommandResult(home);

            // The delivered practice order is already closed and simply leaves the Live Screen after the
            // delivered window; this only closes anything still open so no demo outlives the journey.
            await TryFinishDemoAsync(tenantId, userId, ct).ConfigureAwait(false);
            return new GuidedSetupCommandResult(home, SuccessMessageKey: TrainingCompletedMessageKey);
        });

    public async Task RecordPracticeProgressAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, OrderStatus status, CancellationToken ct)
    {
        try
        {
            var plan = GuidedSetupPlan.SectionsFor(await GetCapabilitiesAsync(user).ConfigureAwait(false));
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (IsInOrderTraining(state, plan))
                await SaveTrainingStepAsync(tenantId, userId, state, GuidedTrainingSteps.ForDemoStatus(status), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The step is also derived from the practice order, so a missed save only loses a resume hint.
            _logger.LogWarning("Order training progress could not be saved: {ExceptionType}", ex.GetType().Name);
        }
    }

    private static int DeliveredWindowMinutes => (int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes;

    private static bool IsInOrderTraining(GuidedSetupState state, IReadOnlyList<string> plan) =>
        state.Status == GuidedSetupStatus.InProgress
        && plan.Contains(GuidedSetupSections.LiveScreenDemo, StringComparer.Ordinal)
        && ResolveSection(state.SectionKey, plan) == GuidedSetupSections.LiveScreenDemo;

    /// <summary>Saves a later training step; an earlier or equal one (e.g. a late request) changes nothing.</summary>
    private async Task SaveTrainingStepAsync(Guid tenantId, Guid userId, GuidedSetupState state, string? step, CancellationToken ct)
    {
        var sameSection = string.Equals(state.SectionKey, GuidedSetupSections.LiveScreenDemo, StringComparison.Ordinal);
        if (step is null || (sameSection && !GuidedTrainingSteps.IsAfter(step, state.StepKey)))
            return;

        await _guidedSetup
            .SaveProgressAsync(tenantId, userId, new GuidedSetupPosition(GuidedSetupSections.LiveScreenDemo, step), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The saved section when the user may still use it; otherwise the next permitted section in
    /// journey order, or the first permitted one when nothing follows.
    /// </summary>
    public static string? ResolveSection(string? savedSectionKey, IReadOnlyList<string> plan)
    {
        if (plan.Count == 0)
            return null;
        if (savedSectionKey is not null && plan.Contains(savedSectionKey, StringComparer.Ordinal))
            return savedSectionKey;

        var journey = GuidedSetupSections.All;
        var savedIndex = savedSectionKey is null ? -1 : IndexOf(journey, savedSectionKey);
        for (var i = savedIndex + 1; i < journey.Count; i++)
        {
            if (plan.Contains(journey[i], StringComparer.Ordinal))
                return journey[i];
        }

        return plan[0];
    }

    public static string? NextSection(string sectionKey, IReadOnlyList<string> plan)
    {
        var index = IndexOf(plan, sectionKey);
        return index >= 0 && index + 1 < plan.Count ? plan[index + 1] : null;
    }

    /// <summary>The page for a section.</summary>
    public static string UrlFor(string? sectionKey, string homeUrl) => sectionKey switch
    {
        GuidedSetupSections.PlatformConnections => "/platform-connections",
        GuidedSetupSections.PrintBridge => "/print-bridge/setup",
        GuidedSetupSections.LiveScreenDemo => LiveScreenUrl,
        _ => homeUrl
    };

    public static string HomeUrlFor(TenantNavigationPermissions permissions) =>
        permissions.CanViewReports ? "/dashboard" : "/orders";

    private async Task<string?> EnsurePermittedSectionAsync(
        Guid tenantId, Guid userId, GuidedSetupState state, IReadOnlyList<string> plan, CancellationToken ct)
    {
        var current = ResolveSection(state.SectionKey, plan);
        if (current is not null && !string.Equals(current, state.SectionKey, StringComparison.Ordinal))
        {
            // The saved section is no longer allowed: move on to the section the user can use.
            await _guidedSetup
                .SaveProgressAsync(tenantId, userId, new GuidedSetupPosition(current, IntroStep), ct)
                .ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>
    /// Skips guided setup for this user, which also takes a tenant still in Setup live (in the same transaction),
    /// then opens the normal Live Screen. Its first snapshot after the page load is the notification baseline, so
    /// orders that arrived during setup appear without sound, notification or highlight.
    /// </summary>
    private async Task<GuidedSetupCommandResult> SkipAndCloseAsync(
        Guid tenantId, Guid userId, TenantNavigationPermissions permissions, string home, CancellationToken ct)
    {
        var result = await _guidedSetup.SkipAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return new GuidedSetupCommandResult(home);

        // A practice order left open must not linger once guidance is closed.
        await TryFinishDemoAsync(tenantId, userId, ct).ConfigureAwait(false);
        var destination = permissions.CanViewLiveScreen ? LiveScreenUrl : home;
        return new GuidedSetupCommandResult(destination, SuccessMessageKey: ClosedMessageKey);
    }

    /// <summary>Closes the user's open practice order. The journey's own state is already saved, so a failure only logs.</summary>
    private async Task TryFinishDemoAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        try
        {
            await _demos.FinishActiveAsync(tenantId, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Guided demo could not be finished after guided setup was closed: {ExceptionType}",
                ex.GetType().Name);
        }
    }

    private Task<GuidedSetupCommandResult> RunAsync(
        ClaimsPrincipal user,
        string command,
        Func<TenantNavigationPermissions, string, Task<GuidedSetupCommandResult>> action) =>
        RunAsync(user, command, homeUrl: null, action);

    /// <param name="homeUrl">Where the command returns, including on failure; the user's home page when null.</param>
    private async Task<GuidedSetupCommandResult> RunAsync(
        ClaimsPrincipal user,
        string command,
        string? homeUrl,
        Func<TenantNavigationPermissions, string, Task<GuidedSetupCommandResult>> action)
    {
        var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
        var home = homeUrl ?? HomeUrlFor(permissions);
        try
        {
            return await action(permissions, home).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Guided setup command {GuidedSetupCommand} failed: {ExceptionType}",
                command,
                ex.GetType().Name);
            return new GuidedSetupCommandResult(home, ErrorMessageKey: FailedMessageKey);
        }
    }

    /// <summary>Guidance fails closed: a read error hides it and never breaks the page.</summary>
    private async Task<GuidedSetupState?> TryReadAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        try
        {
            return await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                "Guided setup state could not be read; guidance is hidden: {ExceptionType}",
                ex.GetType().Name);
            return null;
        }
    }

    private async Task<bool> IsReadyAsync(Guid tenantId, Guid userId, string sectionKey, CancellationToken ct)
    {
        if (sectionKey is not (GuidedSetupSections.PlatformConnections or GuidedSetupSections.PrintBridge))
            return false;

        try
        {
            // The same persisted facts the operational setup checklist uses; nothing is contacted.
            var status = await _setupStatus.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            var step = sectionKey == GuidedSetupSections.PlatformConnections ? status.Platform : status.Printing;
            return step.IsAvailable && step.IsComplete;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Guided setup readiness could not be read: {ExceptionType}", ex.GetType().Name);
            return false;
        }
    }

    private static int IndexOf(IReadOnlyList<string> keys, string key)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (string.Equals(keys[i], key, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
