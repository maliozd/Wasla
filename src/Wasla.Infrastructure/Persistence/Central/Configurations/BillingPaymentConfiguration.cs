using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>
/// WAS-147. A verified payment is immutable, unique by its provider reference within one provider environment, and
/// at most one per subscription period start; its provider and environment are the subscription's (composite key).
/// </summary>
public sealed class BillingPaymentConfiguration : IEntityTypeConfiguration<BillingPayment>
{
    public void Configure(EntityTypeBuilder<BillingPayment> builder)
    {
        builder.ToTable("BillingPayments", table =>
        {
            table.HasCheckConstraint("CK_BillingPayments_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_BillingPayments_Period", "[PeriodEndUtc] > [PeriodStartUtc]");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Provider).HasConversion<int>();
        builder.Property(x => x.Environment).HasConversion<int>();
        builder.Property(x => x.ExternalPaymentReference).IsRequired().HasMaxLength(BillingSubscription.ReferenceMaxLength);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(BillingPrice.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Ignore(x => x.Price);

        foreach (var property in builder.Metadata.GetProperties().Where(p => !p.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasIndex(x => new { x.Provider, x.Environment, x.ExternalPaymentReference })
            .IsUnique()
            .HasDatabaseName("IX_BillingPayments_ProviderReference");

        builder.HasIndex(x => new { x.SubscriptionId, x.PeriodStartUtc })
            .IsUnique()
            .HasDatabaseName("IX_BillingPayments_SubscriptionId_PeriodStartUtc");

        builder.HasOne<BillingSubscription>()
            .WithMany()
            .HasForeignKey(x => new { x.SubscriptionId, x.Provider, x.Environment })
            .HasPrincipalKey(x => new { x.Id, x.Provider, x.Environment })
            .HasConstraintName("FK_BillingPayments_BillingSubscriptions")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
