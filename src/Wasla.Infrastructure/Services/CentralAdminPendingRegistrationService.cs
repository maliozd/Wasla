using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

public sealed class CentralAdminPendingRegistrationService : ICentralAdminPendingRegistrationService
{
    private readonly CentralDbContext _db;

    public CentralAdminPendingRegistrationService(CentralDbContext db)
    {
        _db = db;
    }

    public async Task<AdminPendingRegistrationCountsDto> GetCountsAsync(CancellationToken ct)
    {
        var groups = await _db.PendingRegistrations
            .AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = groups.Sum(g => g.Count);
        var pending = groups.Where(g => g.Status == PendingRegistrationStatus.AwaitingPayment).Sum(g => g.Count);
        var paymentReceived = groups.Where(g => g.Status == PendingRegistrationStatus.PaymentSucceeded).Sum(g => g.Count);
        var provisioned = groups.Where(g => g.Status == PendingRegistrationStatus.Provisioned).Sum(g => g.Count);

        return new AdminPendingRegistrationCountsDto
        {
            Total = total,
            PendingPayment = pending,
            PaymentReceivedSetupPending = paymentReceived,
            Provisioned = provisioned
        };
    }

    public async Task<AdminPendingRegistrationListResult> GetListAsync(
        PendingRegistrationAdminFilter filter,
        CancellationToken ct)
    {
        var query = _db.PendingRegistrations.AsNoTracking();

        query = filter switch
        {
            PendingRegistrationAdminFilter.PendingPayment =>
                query.Where(r => r.Status == PendingRegistrationStatus.AwaitingPayment),
            PendingRegistrationAdminFilter.PaymentReceivedSetupPending =>
                query.Where(r => r.Status == PendingRegistrationStatus.PaymentSucceeded),
            PendingRegistrationAdminFilter.Provisioned =>
                query.Where(r => r.Status == PendingRegistrationStatus.Provisioned),
            _ => query
        };

        var rows = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Fetch provisioned tenant domains in one query
        var tenantIds = rows
            .Where(r => r.TenantId.HasValue)
            .Select(r => r.TenantId!.Value)
            .Distinct()
            .ToList();

        var tenantDomains = tenantIds.Count > 0
            ? await _db.Tenants.AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.PrimaryDomain, ct)
                .ConfigureAwait(false)
            : new Dictionary<Guid, string>();

        var items = rows.Select(r => MapListItem(r, tenantDomains)).ToList();

        return new AdminPendingRegistrationListResult
        {
            Items = items,
            AppliedFilter = filter
        };
    }

    public async Task<IReadOnlyList<AdminPendingRegistrationListItemDto>> GetAttentionListAsync(CancellationToken ct)
    {
        var rows = await _db.PendingRegistrations
            .AsNoTracking()
            .Where(r => r.Status == PendingRegistrationStatus.PaymentSucceeded && r.TenantId == null)
            .OrderBy(r => r.PaymentSucceededAtUtc ?? r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .Take(20)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => MapListItem(r, new Dictionary<Guid, string>())).ToList();
    }

    public async Task<AdminPendingRegistrationDetailDto?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var r = await _db.PendingRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false);

        if (r is null) return null;

        string? tenantDomain = null;
        if (r.TenantId.HasValue)
        {
            var t = await _db.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == r.TenantId.Value, ct)
                .ConfigureAwait(false);
            tenantDomain = t?.PrimaryDomain;
        }

        return new AdminPendingRegistrationDetailDto
        {
            Id = r.Id,
            BusinessName = r.BusinessName,
            Slug = r.Slug,
            PrimaryDomain = r.PrimaryDomain,
            PlanCode = r.PlanCode,
            BillingPeriod = r.BillingPeriod,
            Country = r.Country,
            City = r.City,
            District = r.District,
            Neighborhood = r.Neighborhood,
            StreetAddress = r.StreetAddress,
            OwnerFullName = r.OwnerFullName,
            OwnerEmail = r.OwnerEmail,
            OwnerPhone = r.OwnerPhone,
            BusinessPhone = r.BusinessPhone,
            BusinessEmail = r.BusinessEmail,
            Status = r.Status,
            CreatedAtUtc = r.CreatedAtUtc,
            ExpiresAtUtc = r.ExpiresAtUtc,
            PaymentSucceededAtUtc = r.PaymentSucceededAtUtc,
            PaymentFailedAtUtc = r.PaymentFailedAtUtc,
            ProvisionedAtUtc = r.ProvisionedAtUtc,
            TenantId = r.TenantId,
            TenantDomain = tenantDomain,
            IsEligibleForProvisioning = r.Status == PendingRegistrationStatus.PaymentSucceeded
                && r.TenantId == null
        };
    }

    private static AdminPendingRegistrationListItemDto MapListItem(
        Domain.Entities.Central.PendingRegistration r,
        Dictionary<Guid, string> tenantDomains)
    {
        tenantDomains.TryGetValue(r.TenantId ?? Guid.Empty, out var domain);
        return new AdminPendingRegistrationListItemDto
        {
            Id = r.Id,
            BusinessName = r.BusinessName,
            Slug = r.Slug,
            OwnerFullName = r.OwnerFullName,
            OwnerEmail = r.OwnerEmail,
            PlanCode = r.PlanCode,
            BillingPeriod = r.BillingPeriod,
            Status = r.Status,
            CreatedAtUtc = r.CreatedAtUtc,
            PaymentSucceededAtUtc = r.PaymentSucceededAtUtc,
            ProvisionedAtUtc = r.ProvisionedAtUtc,
            TenantId = r.TenantId,
            TenantDomain = domain,
            IsEligibleForProvisioning = r.Status == PendingRegistrationStatus.PaymentSucceeded
                && r.TenantId == null
        };
    }
}
