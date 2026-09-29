using System.Security.Claims;
using Wasla.Application.Abstractions.GuidedSetup;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Demos;
using Wasla.Application.GuidedSetup;
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

    /// <summary>The first-use Skip. It never ends a journey that has started; that needs <see cref="EndAsync"/>.</summary>
    Task<GuidedSetupCommandResult> SkipAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>Ends a started journey permanently (a Skip). Requires the user's explicit confirmation.</summary>
    Task<GuidedSetupCommandResult> EndAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, bool confirmed, CancellationToken ct);

    Task<GuidedSetupCommandResult> ContinueAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct);

    /// <summary>Leaves <paramref name="fromSectionKey"/> (ready or set up later) for the next permitted section.</summary>
    Task<GuidedSetupCommandResult> AdvanceAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, string? fromSectionKey, CancellationToken ct);
}

/// <summary>Where to go after a command, and an optional localized message key.</summary>
public sealed record GuidedSetupCommandResult(string RedirectUrl, string? SuccessMessageKey = null, string? ErrorMessageKey = null);

public sealed class GuidedSetupCoordinator : IGuidedSetupCoordinator
{
    /// <summary>Each Phase 2 section is one page-level panel, so it has a single step.</summary>
    public const string IntroStep = "intro";

    public const string ClosedMessageKey = "GuidedSetup.Closed";
    public const string FailedMessageKey = "GuidedSetup.ActionFailed";
    public const string UnavailableMessageKey = "GuidedSetup.Unavailable";

    private readonly IGuidedSetupService _guidedSetup;
    private readonly ITenantNavigationAuthorizationService _navigation;
    private readonly ITenantSetupStatusService _setupStatus;
    private readonly IGuidedDemoService _demos;
    private readonly ILogger<GuidedSetupCoordinator> _logger;

    public GuidedSetupCoordinator(
        IGuidedSetupService guidedSetup,
        ITenantNavigationAuthorizationService navigation,
        ITenantSetupStatusService setupStatus,
        IGuidedDemoService demos,
        ILogger<GuidedSetupCoordinator> logger)
    {
        _guidedSetup = guidedSetup;
        _navigation = navigation;
        _setupStatus = setupStatus;
        _demos = demos;
        _logger = logger;
    }

    public async Task<GuidedSetupCapabilities> GetCapabilitiesAsync(ClaimsPrincipal user) =>
        ToCapabilities(await _navigation.GetPermissionsAsync(user).ConfigureAwait(false));

    /// <summary>
    /// Platform guidance follows the policy PlatformConnectionsController enforces. The Print Bridge
    /// section creates device tokens, so it needs both Print Bridge policies; with only one of them
    /// the user gets no Print Bridge section instead of a partial journey.
    /// </summary>
    public static GuidedSetupCapabilities ToCapabilities(TenantNavigationPermissions permissions) =>
        new(
            CanManagePlatformConnections: permissions.CanManageTenantSettings,
            CanManagePrintBridge: permissions.CanManagePrintBridgeDevices && permissions.CanManageDeviceSecurity,
            CanManageOrders: permissions.CanManageOrders);

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
            return new GuidedSetupCommandResult(UrlFor(current, home));
        });

    public Task<GuidedSetupCommandResult> SkipAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "skip", async (_, home) =>
        {
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (state.Status != GuidedSetupStatus.NotStarted)
            {
                // A started journey is only ended through the confirmed End action.
                return new GuidedSetupCommandResult(home);
            }

            return await SkipAndCloseAsync(tenantId, userId, home, ct).ConfigureAwait(false);
        });

    public Task<GuidedSetupCommandResult> EndAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, bool confirmed, CancellationToken ct) =>
        RunAsync(user, "end", async (_, home) =>
        {
            if (!confirmed)
                return new GuidedSetupCommandResult(home);

            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (state.Status != GuidedSetupStatus.InProgress)
                return new GuidedSetupCommandResult(home);

            return await SkipAndCloseAsync(tenantId, userId, home, ct).ConfigureAwait(false);
        });

    public Task<GuidedSetupCommandResult> ContinueAsync(Guid tenantId, Guid userId, ClaimsPrincipal user, CancellationToken ct) =>
        RunAsync(user, "continue", async (permissions, home) =>
        {
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (plan.Count == 0 || state.Status != GuidedSetupStatus.InProgress)
                return new GuidedSetupCommandResult(home);

            var current = await EnsurePermittedSectionAsync(tenantId, userId, state, plan, ct).ConfigureAwait(false);
            return new GuidedSetupCommandResult(UrlFor(current, home));
        });

    public Task<GuidedSetupCommandResult> AdvanceAsync(
        Guid tenantId, Guid userId, ClaimsPrincipal user, string? fromSectionKey, CancellationToken ct) =>
        RunAsync(user, "advance", async (permissions, home) =>
        {
            var plan = GuidedSetupPlan.SectionsFor(ToCapabilities(permissions));
            var state = await _guidedSetup.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (plan.Count == 0 || state.Status != GuidedSetupStatus.InProgress)
                return new GuidedSetupCommandResult(home);

            var current = ResolveSection(state.SectionKey, plan)!;
            if (!string.Equals(fromSectionKey, current, StringComparison.Ordinal))
            {
                // A stale page (another tab moved on): show the real current section, change nothing.
                return new GuidedSetupCommandResult(UrlFor(current, home));
            }

            var next = NextSection(current, plan);
            if (next is null)
                return new GuidedSetupCommandResult(UrlFor(current, home));

            // Setting a section up later only moves the position; it never marks anything as set up.
            var saved = await _guidedSetup
                .SaveProgressAsync(tenantId, userId, new GuidedSetupPosition(next, IntroStep), ct)
                .ConfigureAwait(false);
            return saved.Succeeded
                ? new GuidedSetupCommandResult(UrlFor(next, home))
                : new GuidedSetupCommandResult(home, ErrorMessageKey: FailedMessageKey);
        });

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

    /// <summary>
    /// The page for a section. Order training is not available in this phase, so it hands off to
    /// the home page, which shows that it is next; the next phase points it at the Live Screen.
    /// </summary>
    public static string UrlFor(string? sectionKey, string homeUrl) => sectionKey switch
    {
        GuidedSetupSections.PlatformConnections => "/platform-connections",
        GuidedSetupSections.PrintBridge => "/print-bridge/setup",
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

    private async Task<GuidedSetupCommandResult> SkipAndCloseAsync(Guid tenantId, Guid userId, string home, CancellationToken ct)
    {
        var result = await _guidedSetup.SkipAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return new GuidedSetupCommandResult(home);

        try
        {
            // A practice order left open from the earlier training must not linger once guidance is closed.
            await _demos.FinishActiveAsync(tenantId, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Guided demo could not be finished after guided setup was closed: {ExceptionType}",
                ex.GetType().Name);
        }

        return new GuidedSetupCommandResult(home, SuccessMessageKey: ClosedMessageKey);
    }

    private async Task<GuidedSetupCommandResult> RunAsync(
        ClaimsPrincipal user,
        string command,
        Func<TenantNavigationPermissions, string, Task<GuidedSetupCommandResult>> action)
    {
        var permissions = await _navigation.GetPermissionsAsync(user).ConfigureAwait(false);
        var home = HomeUrlFor(permissions);
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
