using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Admin;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// CentralDb-only reads for the Admin Tenant Operations Center. Nothing here opens a tenant database: the overview
/// runs a fixed number of aggregate queries and a list page runs at most four, whatever the page size or tenant count.
/// Every query projects the columns it shows, so encrypted connection strings, token hashes and password hashes are
/// never read into memory.
/// </summary>
public sealed class CentralAdminTenantOperationsService : ICentralAdminTenantOperationsService
{
    private const int RecentTenantCount = 5;
    private const int MaxDevicesPerTenant = 50;

    private readonly CentralDbContext _db;
    private readonly TimeProvider _time;

    public CentralAdminTenantOperationsService(CentralDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<TenantOperationsOverview> GetOverviewAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var tenantCountRows = await _db.Tenants
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Active = g.Count(t => t.IsActive),
                MigrationFailed = g.Count(t => t.LastMigrationResult != null
                    && t.LastMigrationResult.StartsWith(TenantMigrationRecords.FailedPrefix)),
                MigrationNotRecorded = g.Count(t => t.LastMigrationResult == null || t.LastMigrationResult == "")
            })
            // A constant-key group returns at most one row; ToList avoids an unordered-row-limit (TOP 1) query.
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var tenantCounts = tenantCountRows.SingleOrDefault();

        var registrationGroups = await _db.PendingRegistrations
            .AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), OldestPayment = g.Min(r => r.PaymentSucceededAtUtc) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var onlineSince = now - PrintBridgeConnectionStatusCalculator.ConnectedThreshold;
        var staleSince = now - PrintBridgeConnectionStatusCalculator.RecentlySeenThreshold;
        var deviceRows = await _db.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.RemovedAtUtc == null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Disabled = g.Count(d => !d.IsActive),
                NeverConnected = g.Count(d => d.IsActive && d.LastSeenAt == null),
                Online = g.Count(d => d.IsActive && d.LastSeenAt >= onlineSince),
                Stale = g.Count(d => d.IsActive && d.LastSeenAt < onlineSince && d.LastSeenAt >= staleSince),
                Offline = g.Count(d => d.IsActive && d.LastSeenAt < staleSince)
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var devices = deviceRows.SingleOrDefault();

        var recent = await _db.Tenants
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Slug)
            .Take(RecentTenantCount)
            .Select(t => new RecentTenantItem(t.Id, t.Name, t.Slug, t.IsActive, t.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int Registrations(params PendingRegistrationStatus[] statuses) =>
            registrationGroups.Where(g => statuses.Contains(g.Status)).Sum(g => g.Count);

        var total = tenantCounts?.Total ?? 0;
        var active = tenantCounts?.Active ?? 0;

        return new TenantOperationsOverview(
            new TenantRegistryCounts(
                total,
                active,
                total - active,
                tenantCounts?.MigrationFailed ?? 0,
                tenantCounts?.MigrationNotRecorded ?? 0),
            new RegistrationPipelineCounts(
                Registrations(PendingRegistrationStatus.AwaitingPayment),
                Registrations(PendingRegistrationStatus.PaymentSucceeded),
                registrationGroups.FirstOrDefault(g => g.Status == PendingRegistrationStatus.PaymentSucceeded)?.OldestPayment,
                Registrations(PendingRegistrationStatus.PaymentFailed),
                Registrations(PendingRegistrationStatus.Expired, PendingRegistrationStatus.Cancelled),
                Registrations(PendingRegistrationStatus.Provisioned)),
            new PrintBridgeFleetCounts(
                devices?.Online ?? 0,
                devices?.Stale ?? 0,
                devices?.Offline ?? 0,
                devices?.NeverConnected ?? 0,
                devices?.Disabled ?? 0),
            recent,
            now);
    }

    public async Task<TenantListPage> GetTenantListAsync(TenantListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var now = _time.GetUtcNow().UtcDateTime;

        var rows =
            from tenant in _db.Tenants.AsNoTracking()
            join membership in _db.TenantMemberships.AsNoTracking() on tenant.Id equals membership.TenantId into memberships
            from membership in memberships.DefaultIfEmpty()
            select new TenantRow
            {
                Tenant = tenant,
                PlanCode = membership != null ? membership.PlanCode : null,
                MembershipStatus = membership != null ? (MembershipStatus?)membership.Status : null
            };

        rows = ApplyFilters(rows, query);

        var totalCount = await rows.CountAsync(ct).ConfigureAwait(false);
        var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)query.PageSize);
        var applied = query with { Page = Math.Min(query.Page, totalPages) };

        var pageRows = await ApplySort(rows, applied)
            .Skip((applied.Page - 1) * applied.PageSize)
            .Take(applied.PageSize)
            .Select(row => new
            {
                row.Tenant.Id,
                row.Tenant.Name,
                row.Tenant.Slug,
                row.Tenant.PrimaryDomain,
                row.Tenant.IsActive,
                row.PlanCode,
                row.MembershipStatus,
                MigrationSucceeded = row.Tenant.LastMigrationResult == TenantMigrationRecords.SuccessValue,
                MigrationFailed = row.Tenant.LastMigrationResult != null
                    && row.Tenant.LastMigrationResult.StartsWith(TenantMigrationRecords.FailedPrefix),
                MigrationRecorded = row.Tenant.LastMigrationResult != null && row.Tenant.LastMigrationResult != "",
                row.Tenant.CreatedAt,
                row.Tenant.UpdatedAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var deviceCounts = new Dictionary<Guid, (int Active, int Online)>();
        if (pageRows.Count > 0)
        {
            var ids = pageRows.Select(row => row.Id).ToList();
            var onlineSince = now - PrintBridgeConnectionStatusCalculator.ConnectedThreshold;
            var grouped = await _db.PrintBridgeDevices
                .AsNoTracking()
                .Where(d => ids.Contains(d.TenantId) && d.RemovedAtUtc == null && d.IsActive)
                .GroupBy(d => d.TenantId)
                .Select(g => new { TenantId = g.Key, Active = g.Count(), Online = g.Count(d => d.LastSeenAt >= onlineSince) })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var group in grouped)
                deviceCounts[group.TenantId] = (group.Active, group.Online);
        }

        var items = pageRows
            .Select(row =>
            {
                deviceCounts.TryGetValue(row.Id, out var devices);
                return new TenantListItem(
                    row.Id,
                    row.Name,
                    row.Slug,
                    row.PrimaryDomain,
                    row.IsActive,
                    row.PlanCode,
                    row.MembershipStatus,
                    ToMigrationRecord(row.MigrationRecorded, row.MigrationSucceeded, row.MigrationFailed),
                    row.CreatedAt,
                    row.UpdatedAt,
                    devices.Active,
                    devices.Online);
            })
            .ToList();

        var registryIsEmpty = totalCount == 0
            && (!query.HasFilters || !await _db.Tenants.AsNoTracking().AnyAsync(ct).ConfigureAwait(false));

        return new TenantListPage(applied, items, totalCount, registryIsEmpty);
    }

    public async Task<TenantOperationsDetail?> GetTenantDetailAsync(Guid tenantId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var tenant = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.PrimaryDomain,
                t.DatabaseName,
                t.IsActive,
                t.CreatedAt,
                t.UpdatedAt,
                t.LastMigrationAt,
                // Only whether connection details exist; the encrypted value itself never leaves the database.
                DatabaseConfigured = t.EncryptedConnectionString != null && t.EncryptedConnectionString != "",
                MigrationSucceeded = t.LastMigrationResult == TenantMigrationRecords.SuccessValue,
                MigrationFailed = t.LastMigrationResult != null
                    && t.LastMigrationResult.StartsWith(TenantMigrationRecords.FailedPrefix),
                MigrationRecorded = t.LastMigrationResult != null && t.LastMigrationResult != ""
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (tenant is null)
            return null;

        var membership = await _db.TenantMemberships
            .AsNoTracking()
            .Where(m => m.TenantId == tenant.Id)
            .Select(m => new TenantMembershipSummary(
                m.PlanCode,
                m.Status,
                m.BillingPeriod,
                m.StartedAt,
                m.TrialEndsAt,
                m.CurrentPeriodEndsAt))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var registration = await _db.PendingRegistrations
            .AsNoTracking()
            .Where(r => r.TenantId == tenant.Id)
            .OrderByDescending(r => r.ProvisionedAtUtc)
            .ThenByDescending(r => r.CreatedAtUtc)
            .Select(r => new TenantRegistrationSummary(
                r.Id,
                r.Status,
                r.CreatedAtUtc,
                r.PaymentSucceededAtUtc,
                r.PaymentFailedAtUtc,
                r.ProvisionedAtUtc))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var devices = await _db.PrintBridgeDevices
            .AsNoTracking()
            .Where(d => d.TenantId == tenant.Id && d.RemovedAtUtc == null)
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.Name)
            .ThenBy(d => d.Id)
            .Take(MaxDevicesPerTenant)
            .Select(d => new { d.Name, d.IsActive, d.LastSeenAt, d.AppVersion })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new TenantOperationsDetail(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.PrimaryDomain,
            tenant.DatabaseName,
            tenant.IsActive,
            tenant.CreatedAt,
            tenant.UpdatedAt,
            tenant.DatabaseConfigured,
            ToMigrationRecord(tenant.MigrationRecorded, tenant.MigrationSucceeded, tenant.MigrationFailed),
            tenant.LastMigrationAt,
            membership,
            registration,
            devices
                .Select(d => new TenantPrintBridgeDeviceSummary(
                    d.Name,
                    d.IsActive,
                    d.LastSeenAt,
                    d.AppVersion,
                    PrintBridgeConnectionStatusCalculator.Calculate(d.IsActive, d.LastSeenAt, now)))
                .ToList(),
            now);
    }

    private static IQueryable<TenantRow> ApplyFilters(IQueryable<TenantRow> rows, TenantListQuery query)
    {
        if (query.Search is { } search)
        {
            var term = search.ToLowerInvariant();
            rows = rows.Where(row =>
                row.Tenant.Name.ToLower().Contains(term)
                || row.Tenant.Slug.ToLower().Contains(term)
                || row.Tenant.PrimaryDomain.ToLower().Contains(term));
        }

        rows = query.Status switch
        {
            TenantListStatusFilter.Active => rows.Where(row => row.Tenant.IsActive),
            TenantListStatusFilter.Inactive => rows.Where(row => !row.Tenant.IsActive),
            _ => rows
        };

        rows = query.Migration switch
        {
            TenantMigrationRecordFilter.Succeeded => rows.Where(row =>
                row.Tenant.LastMigrationResult == TenantMigrationRecords.SuccessValue),
            TenantMigrationRecordFilter.Failed => rows.Where(row =>
                row.Tenant.LastMigrationResult != null
                && row.Tenant.LastMigrationResult.StartsWith(TenantMigrationRecords.FailedPrefix)),
            TenantMigrationRecordFilter.NotRecorded => rows.Where(row =>
                row.Tenant.LastMigrationResult == null || row.Tenant.LastMigrationResult == ""),
            _ => rows
        };

        if (query.Plan is { } plan)
        {
            rows = plan == TenantListQuery.NoPlan
                ? rows.Where(row => row.PlanCode == null)
                : rows.Where(row => row.PlanCode == plan);
        }

        return rows;
    }

    /// <summary>
    /// Sorts only by allowlisted columns. Slug is unique, so the secondary order (slug, then id) makes every page
    /// deterministic even when the primary values tie.
    /// </summary>
    private static IQueryable<TenantRow> ApplySort(IQueryable<TenantRow> rows, TenantListQuery query)
    {
        var ascending = query.Direction == TenantListSortDirection.Ascending;
        IOrderedQueryable<TenantRow> ordered = query.Sort switch
        {
            TenantListSortField.Name => ascending
                ? rows.OrderBy(row => row.Tenant.Name)
                : rows.OrderByDescending(row => row.Tenant.Name),
            TenantListSortField.Slug => ascending
                ? rows.OrderBy(row => row.Tenant.Slug)
                : rows.OrderByDescending(row => row.Tenant.Slug),
            TenantListSortField.UpdatedAt => ascending
                ? rows.OrderBy(row => row.Tenant.UpdatedAt)
                : rows.OrderByDescending(row => row.Tenant.UpdatedAt),
            // Ascending status lists active tenants first.
            TenantListSortField.Status => ascending
                ? rows.OrderByDescending(row => row.Tenant.IsActive)
                : rows.OrderBy(row => row.Tenant.IsActive),
            _ => ascending
                ? rows.OrderBy(row => row.Tenant.CreatedAt)
                : rows.OrderByDescending(row => row.Tenant.CreatedAt)
        };

        return ordered
            .ThenBy(row => row.Tenant.Slug)
            .ThenBy(row => row.Tenant.Id);
    }

    private static TenantMigrationRecord ToMigrationRecord(bool recorded, bool succeeded, bool failed) =>
        !recorded
            ? TenantMigrationRecord.NotRecorded
            : failed
                ? TenantMigrationRecord.Failed
                : succeeded ? TenantMigrationRecord.Succeeded : TenantMigrationRecord.Unknown;

    private sealed class TenantRow
    {
        public CentralTenant Tenant { get; init; } = null!;
        public string? PlanCode { get; init; }
        public MembershipStatus? MembershipStatus { get; init; }
    }
}
