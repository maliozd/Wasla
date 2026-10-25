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

    /// <remarks>
    /// One CentralDb query projecting the columns the list shows (WAS-38). The password hash, payment reference,
    /// owner contact data and address are never selected, and no tenant row is read.
    /// </remarks>
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

        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .Select(r => new AdminPendingRegistrationListItemDto
            {
                Id = r.Id,
                BusinessName = r.BusinessName,
                Slug = r.Slug,
                OwnerFullName = r.OwnerFullName,
                PlanCode = r.PlanCode,
                BillingPeriod = r.BillingPeriod,
                Status = r.Status,
                CreatedAtUtc = r.CreatedAtUtc,
                PaymentSucceededAtUtc = r.PaymentSucceededAtUtc,
                ProvisionedAtUtc = r.ProvisionedAtUtc,
                TenantId = r.TenantId,
                IsEligibleForProvisioning = r.Status == PendingRegistrationStatus.PaymentSucceeded && r.TenantId == null
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new AdminPendingRegistrationListResult
        {
            Items = items,
            AppliedFilter = filter
        };
    }

    /// <remarks>
    /// Projects only the columns the Admin overview's attention list shows. The password hash, owner contact data,
    /// address and payment reference are never selected; the owner name of the returned items stays empty.
    /// </remarks>
    public async Task<IReadOnlyList<AdminPendingRegistrationListItemDto>> GetAttentionListAsync(CancellationToken ct)
    {
        return await _db.PendingRegistrations
            .AsNoTracking()
            .Where(r => r.Status == PendingRegistrationStatus.PaymentSucceeded && r.TenantId == null)
            .OrderBy(r => r.PaymentSucceededAtUtc ?? r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .Take(20)
            .Select(r => new AdminPendingRegistrationListItemDto
            {
                Id = r.Id,
                BusinessName = r.BusinessName,
                Slug = r.Slug,
                PlanCode = r.PlanCode,
                BillingPeriod = r.BillingPeriod,
                Status = r.Status,
                CreatedAtUtc = r.CreatedAtUtc,
                PaymentSucceededAtUtc = r.PaymentSucceededAtUtc,
                IsEligibleForProvisioning = r.Status == PendingRegistrationStatus.PaymentSucceeded && r.TenantId == null
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <remarks>
    /// One CentralDb query projecting the columns the detail page and the provision action use (WAS-38). The password
    /// hash, payment reference and unshown address lines are never selected, and the linked tenant contributes only
    /// its primary domain, never its connection string. Provisioning reads what it needs itself.
    /// </remarks>
    public async Task<AdminPendingRegistrationDetailDto?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        return await _db.PendingRegistrations
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new AdminPendingRegistrationDetailDto
            {
                Id = r.Id,
                BusinessName = r.BusinessName,
                Slug = r.Slug,
                PrimaryDomain = r.PrimaryDomain,
                DatabaseName = r.DatabaseName,
                PlanCode = r.PlanCode,
                BillingPeriod = r.BillingPeriod,
                Country = r.Country,
                City = r.City,
                District = r.District,
                Neighborhood = r.Neighborhood,
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
                TenantDomain = _db.Tenants
                    .Where(t => t.Id == r.TenantId)
                    .Select(t => t.PrimaryDomain)
                    .FirstOrDefault(),
                IsEligibleForProvisioning = r.Status == PendingRegistrationStatus.PaymentSucceeded
                    && r.TenantId == null
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
