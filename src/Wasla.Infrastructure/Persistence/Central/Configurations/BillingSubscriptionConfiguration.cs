using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>
/// WAS-147. The database repeats the invariants that must survive concurrent writers: one subscription per
/// registration and per tenant, provider references unique per provider environment, billing states only, a provider
/// reference for every verified state, and a positive amount. Snapshot columns throw if EF is asked to change them.
/// </summary>
public sealed class BillingSubscriptionConfiguration : IEntityTypeConfiguration<BillingSubscription>
{
    /// <summary>The integer values of <see cref="SubscriptionStatusTransitions.BillingStates"/>.</summary>
    internal const string BillingStatusValues = "2, 3, 4, 5, 6, 7";

    public void Configure(EntityTypeBuilder<BillingSubscription> builder)
    {
        builder.ToTable("BillingSubscriptions", table =>
        {
            table.HasCheckConstraint("CK_BillingSubscriptions_Status", $"[Status] IN ({BillingStatusValues})");
            table.HasCheckConstraint("CK_BillingSubscriptions_Provider", "[Provider] IN (1)");
            table.HasCheckConstraint("CK_BillingSubscriptions_Environment", "[Environment] IN (1, 2)");
            table.HasCheckConstraint("CK_BillingSubscriptions_BillingPeriod", "[BillingPeriod] IN ('Monthly', 'Yearly')");
            table.HasCheckConstraint("CK_BillingSubscriptions_Amount", "[Amount] > 0");
            table.HasCheckConstraint(
                "CK_BillingSubscriptions_VerifiedStateHasProviderReference",
                "[Status] NOT IN (2, 3) OR [ExternalSubscriptionReference] IS NOT NULL");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        // The payment's provider and environment must be the subscription's; BillingPayments references this key.
        builder.HasAlternateKey(x => new { x.Id, x.Provider, x.Environment })
            .HasName("AK_BillingSubscriptions_Id_Provider_Environment");

        builder.Property(x => x.Provider).HasConversion<int>();
        builder.Property(x => x.Environment).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.PlanCode).IsRequired().HasMaxLength(BillingSubscription.PlanCodeMaxLength);
        builder.Property(x => x.BillingPeriod).IsRequired().HasMaxLength(BillingSubscription.BillingPeriodMaxLength);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(BillingPrice.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.ExternalSubscriptionReference).HasMaxLength(BillingSubscription.ReferenceMaxLength);
        builder.Property(x => x.ExternalSubscriberReference).HasMaxLength(BillingSubscription.ReferenceMaxLength);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.Ignore(x => x.Price);

        foreach (var snapshot in new[]
                 {
                     nameof(BillingSubscription.PendingRegistrationId),
                     nameof(BillingSubscription.PlanCode),
                     nameof(BillingSubscription.BillingPeriod),
                     nameof(BillingSubscription.Amount),
                     nameof(BillingSubscription.Currency),
                     nameof(BillingSubscription.CreatedAtUtc)
                 })
        {
            builder.Property(snapshot).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }

        builder.HasIndex(x => x.PendingRegistrationId)
            .IsUnique()
            .HasDatabaseName("IX_BillingSubscriptions_PendingRegistrationId");

        builder.HasIndex(x => x.TenantId)
            .IsUnique()
            .HasFilter("[TenantId] IS NOT NULL")
            .HasDatabaseName("IX_BillingSubscriptions_TenantId");

        builder.HasIndex(x => new { x.Provider, x.Environment, x.ExternalSubscriptionReference })
            .IsUnique()
            .HasFilter("[ExternalSubscriptionReference] IS NOT NULL")
            .HasDatabaseName("IX_BillingSubscriptions_ProviderReference");

        // A registration is never deleted while it owns billing history.
        builder.HasOne<PendingRegistration>()
            .WithMany()
            .HasForeignKey(x => x.PendingRegistrationId)
            .OnDelete(DeleteBehavior.Restrict);

        // delete-tenant removes the tenant row; billing history stays, still owned by its registration.
        builder.HasOne<CentralTenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
