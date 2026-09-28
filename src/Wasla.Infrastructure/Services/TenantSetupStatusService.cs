using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Setup;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class TenantSetupStatusService : ITenantSetupStatusService
{
    /// <summary>Same singleton row used by operational settings services.</summary>
    private static readonly Guid SettingsSingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly CentralDbContext _central;
    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantSetupStatusService> _logger;

    public TenantSetupStatusService(
        CentralDbContext central,
        ITenantDbContextFactory tenantDbs,
        TimeProvider time,
        ILogger<TenantSetupStatusService> logger)
    {
        _central = central;
        _tenantDbs = tenantDbs;
        _time = time;
        _logger = logger;
    }

    public async Task<TenantSetupStatus> GetAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var restaurant = await ReadRestaurantAsync(tenantId, ct).ConfigureAwait(false);
        var tenantDb = await ReadTenantDatabaseAsync(tenantId, userId, ct).ConfigureAwait(false);
        var printing = await ReadPrintingAsync(tenantId, ct).ConfigureAwait(false);

        return TenantSetupReadiness.Evaluate(
            new TenantSetupFacts(
                restaurant,
                tenantDb.Platforms,
                tenantDb.Notifications,
                printing,
                tenantDb.SetupGuidanceCompleted,
                tenantDb.Team),
            _time.GetUtcNow().UtcDateTime);
    }

    public async Task<bool> CompleteGuidanceAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
            var row = await db.TenantOperationalSettings
                .FirstOrDefaultAsync(settings => settings.Id == SettingsSingletonId, ct)
                .ConfigureAwait(false);

            var now = _time.GetUtcNow().UtcDateTime;
            if (row is null)
            {
                db.TenantOperationalSettings.Add(new Wasla.Domain.Entities.Customer.TenantOperationalSettings
                {
                    Id = SettingsSingletonId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    SetupGuidanceCompletedAtUtc = now
                });
            }
            else if (row.SetupGuidanceCompletedAtUtc is null)
            {
                row.SetupGuidanceCompletedAtUtc = now;
                row.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("SetupGuidance", tenantId, ex);
            return false;
        }
    }

    private async Task<RestaurantProfileFacts> ReadRestaurantAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            var name = await _central.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (name is null)
                return new RestaurantProfileFacts(false, null, null, null, null);

            var membership = await _central.TenantMemberships
                .AsNoTracking()
                .Where(row => row.TenantId == tenantId)
                .Select(row => new { row.BusinessPhone, row.City, row.Country })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return new RestaurantProfileFacts(
                true,
                name,
                membership?.BusinessPhone,
                membership?.City,
                membership?.Country);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("RestaurantProfile", tenantId, ex);
            return new RestaurantProfileFacts(false, null, null, null, null);
        }
    }

    private async Task<TenantDatabaseFacts> ReadTenantDatabaseAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);
            var platforms = await ReadPlatformsAsync(db, tenantId, ct).ConfigureAwait(false);
            var notifications = await ReadNotificationsAsync(db, tenantId, userId, ct).ConfigureAwait(false);
            var guidanceCompleted = await ReadGuidanceCompletedAsync(db, tenantId, ct).ConfigureAwait(false);
            var team = await ReadTeamAsync(db, tenantId, ct).ConfigureAwait(false);
            return new TenantDatabaseFacts(platforms, notifications, guidanceCompleted, team);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("TenantDatabase", tenantId, ex);
            return new TenantDatabaseFacts(
                new PlatformConnectionFacts(false, Array.Empty<PlatformConnectionFact>()),
                new NotificationSetupFacts(false, false, false),
                false,
                new TeamMembershipFacts(false, Array.Empty<TenantTeamMemberFact>()));
        }
    }

    private async Task<PlatformConnectionFacts> ReadPlatformsAsync(
        TenantDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            var rows = await db.PlatformConnections
                .AsNoTracking()
                .Select(connection => new
                {
                    connection.Platform,
                    connection.IsActive,
                    connection.StoreId,
                    HasApiKey = connection.EncryptedApiKey != "",
                    HasApiSecret = connection.EncryptedApiSecret != ""
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var facts = rows
                .Select(row => new PlatformConnectionFact(
                    row.Platform,
                    row.IsActive,
                    row.StoreId,
                    row.HasApiKey,
                    row.HasApiSecret))
                .ToArray();

            return new PlatformConnectionFacts(true, facts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("PlatformConnection", tenantId, ex);
            return new PlatformConnectionFacts(false, Array.Empty<PlatformConnectionFact>());
        }
    }

    private async Task<NotificationSetupFacts> ReadNotificationsAsync(
        TenantDbContext db,
        Guid tenantId,
        Guid userId,
        CancellationToken ct)
    {
        try
        {
            var saved = await db.UserNotificationSettings
                .AsNoTracking()
                .Where(settings => settings.UserId == userId)
                .Select(settings => new { settings.NewOrderSoundEnabled })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (saved is null)
                return new NotificationSetupFacts(true, false, false);

            return new NotificationSetupFacts(true, true, saved.NewOrderSoundEnabled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("Notifications", tenantId, ex);
            return new NotificationSetupFacts(false, false, false);
        }
    }

    private async Task<PrintingSetupFacts> ReadPrintingAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            var rows = await _central.PrintBridgeDevices
                .AsNoTracking()
                .Where(device => device.TenantId == tenantId && device.RemovedAtUtc == null)
                .Select(device => new { device.IsActive, device.LastSeenAt })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var devices = rows
                .Select(row => new PrintingDeviceFact(row.IsActive, row.LastSeenAt))
                .ToArray();

            return new PrintingSetupFacts(true, devices);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("Printing", tenantId, ex);
            return new PrintingSetupFacts(false, Array.Empty<PrintingDeviceFact>());
        }
    }

    private async Task<bool> ReadGuidanceCompletedAsync(
        TenantDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            var completedAt = await db.TenantOperationalSettings
                .AsNoTracking()
                .Where(settings => settings.Id == SettingsSingletonId)
                .Select(settings => settings.SetupGuidanceCompletedAtUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return completedAt is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("SetupGuidance", tenantId, ex);
            return false;
        }
    }

    private async Task<TeamMembershipFacts> ReadTeamAsync(
        TenantDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            var rows = await db.AppUsers
                .AsNoTracking()
                .Select(user => new { user.Id, user.Role, user.IsActive, user.CreatedAt })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var members = rows
                .Select(row => new TenantTeamMemberFact(row.Id, row.Role, row.IsActive, row.CreatedAt))
                .ToArray();

            return new TeamMembershipFacts(true, members);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailure("TeamMembership", tenantId, ex);
            return new TeamMembershipFacts(false, Array.Empty<TenantTeamMemberFact>());
        }
    }

    private sealed record TenantDatabaseFacts(
        PlatformConnectionFacts Platforms,
        NotificationSetupFacts Notifications,
        bool SetupGuidanceCompleted,
        TeamMembershipFacts Team);

    private void LogSourceFailure(string source, Guid tenantId, Exception ex) =>
        _logger.LogWarning(
            "Tenant setup status source {Source} failed for tenant {TenantId}: {ExceptionType}",
            source,
            tenantId,
            ex.GetType().Name);
}
