namespace Wasla.Application.Abstractions.DevelopmentTools;

/// <summary>
/// TEMPORARY, Development only: returns one test tenant's operational and onboarding data to the state it had
/// right after successful provisioning, so onboarding can be tested again from the first Start/Skip choice.
/// People are deliberately kept: provisioning creates only the Owner, but users added since (and their
/// branches) stay, so everyone can still sign in. The Web layer decides
/// whether the tool is available (Development environment, explicit option, tenant Owner) and passes only the
/// tenant resolved from the authenticated request.
/// <para>
/// Removed: all orders and their items, options and print jobs; sync logs and integration errors; every
/// provider connection with its locally stored encrypted credentials and sync cursors; the tenant's operational
/// settings (replaced by the row provisioning writes: default settings, operational mode Setup, so the tenant is
/// not live again until someone completes or skips guided setup); every user's notification settings (the missing
/// row is the canonical post-provisioning default); guided-setup state, practice orders and legacy product-tour
/// completions for every user; and the
/// tenant's Print Bridge devices (removed the way device removal does it, so their tokens stop working) and
/// unfinished Print Bridge setup sessions.
/// </para>
/// <para>
/// Kept: the tenant record, database, slug and domain; every user account, password and role; branches;
/// password-reset tokens; subscription, checkout, payment and provisioning records; migrations. Nothing is
/// revoked at a provider: only Wasla's local configuration is removed.
/// </para>
/// </summary>
public interface ITenantDevelopmentResetService
{
    Task<TenantDevelopmentResetResult> ResetAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// What was removed. When <see cref="Succeeded"/> is false nothing was changed, except in the rare
/// <see cref="PartiallyApplied"/> case (the Central commit succeeded, the tenant commit then failed): the Print
/// Bridge devices were removed and running the reset again completes it.
/// </summary>
public sealed record TenantDevelopmentResetResult(
    bool Succeeded,
    int Orders = 0,
    int PrintJobs = 0,
    int PlatformConnections = 0,
    int PrintBridgeDevices = 0,
    int GuidedSetupStates = 0,
    int PracticeOrders = 0,
    bool PartiallyApplied = false)
{
    public static TenantDevelopmentResetResult Failed { get; } = new(false);

    public static TenantDevelopmentResetResult PrintBridgeOnly { get; } = new(false, PartiallyApplied: true);
}
