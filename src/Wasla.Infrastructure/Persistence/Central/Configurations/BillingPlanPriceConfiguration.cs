using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>
/// WAS-147. Wasla's plan prices: one row per plan, period and effective time, immutable, positive. A subscription
/// references the row it was priced from, so a referenced row cannot be deleted.
/// </summary>
public sealed class BillingPlanPriceConfiguration : IEntityTypeConfiguration<BillingPlanPrice>
{
    public void Configure(EntityTypeBuilder<BillingPlanPrice> builder)
    {
        builder.ToTable("BillingPlanPrices", table =>
        {
            table.HasCheckConstraint("CK_BillingPlanPrices_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_BillingPlanPrices_BillingPeriod", "[BillingPeriod] IN ('Monthly', 'Yearly')");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.PlanCode).IsRequired().HasMaxLength(BillingSubscription.PlanCodeMaxLength);
        builder.Property(x => x.BillingPeriod).IsRequired().HasMaxLength(BillingSubscription.BillingPeriodMaxLength);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(BillingPrice.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Ignore(x => x.Price);

        foreach (var property in builder.Metadata.GetProperties().Where(p => !p.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasIndex(x => new { x.PlanCode, x.BillingPeriod, x.EffectiveFromUtc })
            .IsUnique()
            .HasDatabaseName("IX_BillingPlanPrices_PlanCode_BillingPeriod_EffectiveFromUtc");
    }
}
