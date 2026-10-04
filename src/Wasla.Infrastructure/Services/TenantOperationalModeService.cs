using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Setup;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>Reads the tenant's operational mode and automation settings from the settings row; no row means the defaults (Live).</summary>
public sealed class TenantOperationalModeService : ITenantOperationalModeService
{
    private readonly ITenantDbContextFactory _tenantDbs;

    public TenantOperationalModeService(ITenantDbContextFactory tenantDbs)
    {
        _tenantDbs = tenantDbs;
    }

    public async Task<TenantAutomationStatus> GetAutomationStatusAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
        return await TenantOperationalModes.ReadAutomationStatusAsync(db, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The only writers of <see cref="TenantOperationalSettings.OperationalMode"/>. Setup is written for a brand-new
/// tenant database (provisioning) and by the Development tenant reset; Setup → Live is a conditional update, so it
/// is idempotent and two users finishing guided setup at the same time both succeed. Nothing moves Live back.
/// </summary>
public static class TenantOperationalModes
{
    /// <summary>The settings singleton's id, as every settings service uses it.</summary>
    public static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>One read of the settings row: the mode and the saved automation flags. Never writes.</summary>
    public static async Task<TenantAutomationStatus> ReadAutomationStatusAsync(TenantDbContext db, CancellationToken ct)
    {
        var status = await db.TenantOperationalSettings
            .AsNoTracking()
            .Where(settings => settings.Id == SettingsId)
            .Select(settings => new TenantAutomationStatus(
                settings.OperationalMode,
                settings.OrderSyncEnabled,
                settings.AutoApproveNewOrders,
                settings.AutoPrintReceiptOnAutoApprove))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return status ?? TenantAutomationStatus.WithoutSettings;
    }

    /// <summary>The settings row of a tenant that has just been provisioned: the default settings, in Setup.</summary>
    public static TenantOperationalSettings NewTenantSettings(DateTime nowUtc) =>
        new()
        {
            Id = SettingsId,
            OperationalMode = TenantOperationalMode.Setup,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };

    /// <summary>
    /// Provisioning: a new tenant starts in Setup. Adds the settings row only when there is none, so a repeated
    /// provisioning run never moves a tenant back from Live. Returns whether the row was added.
    /// </summary>
    public static async Task<bool> EnsureNewTenantStartsInSetupAsync(TenantDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var exists = await db.TenantOperationalSettings
            .AnyAsync(settings => settings.Id == SettingsId, ct)
            .ConfigureAwait(false);
        if (exists)
            return false;

        db.TenantOperationalSettings.Add(NewTenantSettings(nowUtc));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Setup → Live, in the caller's transaction when there is one. Returns whether this call changed it.</summary>
    public static async Task<bool> ActivateAsync(TenantDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var changed = await db.TenantOperationalSettings
            .Where(settings => settings.Id == SettingsId && settings.OperationalMode == TenantOperationalMode.Setup)
            .ExecuteUpdateAsync(set => set
                .SetProperty(settings => settings.OperationalMode, TenantOperationalMode.Live)
                .SetProperty(settings => settings.UpdatedAt, nowUtc), ct)
            .ConfigureAwait(false);
        return changed > 0;
    }

    /// <summary>
    /// Setup → Live as a tracked change, saved by the caller's next SaveChanges together with its other changes
    /// (one database transaction). Nothing is tracked when the tenant is already Live.
    /// </summary>
    public static async Task StageActivationAsync(TenantDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var row = await db.TenantOperationalSettings
            .FirstOrDefaultAsync(settings => settings.Id == SettingsId && settings.OperationalMode == TenantOperationalMode.Setup, ct)
            .ConfigureAwait(false);
        if (row is null)
            return;

        row.OperationalMode = TenantOperationalMode.Live;
        row.UpdatedAt = nowUtc;
    }
}
