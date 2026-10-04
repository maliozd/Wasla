using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Values for a tenant's single <see cref="TenantMembership"/> row. The restaurant setup step reads
/// the business phone and location from this row, so every provisioning path must create it.
/// </summary>
public sealed record TenantMembershipSeed(
    string PlanCode,
    string BillingPeriod,
    MembershipStatus Status,
    DateTime? TrialEndsAt,
    string? OwnerEmail,
    string? BusinessPhone,
    string? City,
    string? Country,
    string? BusinessType);

/// <summary>
/// Restaurant contact fields an operator may set. Null leaves the stored value unchanged.
/// </summary>
public sealed record TenantContactUpdate(string? BusinessPhone, string? City, string? Country);

public static class TenantMembershipRecords
{
    public const int BusinessPhoneMaxLength = 50;
    public const int LocationMaxLength = 100;

    /// <summary>
    /// Adds the membership row when the tenant has none. An existing row is never changed.
    /// Returns true when a row was created.
    /// </summary>
    public static async Task<bool> EnsureAsync(
        CentralDbContext central,
        Guid tenantId,
        TenantMembershipSeed seed,
        DateTime utcNow,
        CancellationToken ct)
    {
        var exists = await central.TenantMemberships
            .AnyAsync(membership => membership.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (exists)
            return false;

        central.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PlanCode = seed.PlanCode,
            BillingPeriod = seed.BillingPeriod,
            Status = seed.Status,
            StartedAt = utcNow,
            TrialEndsAt = seed.TrialEndsAt,
            OwnerEmail = seed.OwnerEmail,
            BusinessPhone = seed.BusinessPhone,
            City = seed.City,
            Country = seed.Country,
            BusinessType = seed.BusinessType,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        });
        await central.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sets the provided contact fields, creating the membership row from <paramref name="whenMissing"/>
    /// first when the tenant has none. Repeating the same update changes nothing.
    /// </summary>
    public static async Task UpdateContactAsync(
        CentralDbContext central,
        Guid tenantId,
        TenantContactUpdate update,
        TenantMembershipSeed whenMissing,
        DateTime utcNow,
        CancellationToken ct)
    {
        await EnsureAsync(central, tenantId, whenMissing, utcNow, ct).ConfigureAwait(false);

        var membership = await central.TenantMemberships
            .SingleAsync(row => row.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        var changed = false;
        changed |= Assign(update.BusinessPhone, membership.BusinessPhone, value => membership.BusinessPhone = value);
        changed |= Assign(update.City, membership.City, value => membership.City = value);
        changed |= Assign(update.Country, membership.Country, value => membership.Country = value);
        if (!changed)
            return;

        membership.UpdatedAt = utcNow;
        await central.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool Assign(string? requested, string? current, Action<string> set)
    {
        var value = requested?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, current, StringComparison.Ordinal))
            return false;

        set(value);
        return true;
    }
}
