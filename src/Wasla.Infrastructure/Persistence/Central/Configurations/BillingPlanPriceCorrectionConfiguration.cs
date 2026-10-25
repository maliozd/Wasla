using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

/// <summary>
/// GitHub #64. The audit of plan price corrections: append-only, every column refused by EF after insert, positive
/// amounts. No foreign key to the price row, so a withdrawn scheduled price keeps its correction history.
/// </summary>
public sealed class BillingPlanPriceCorrectionConfiguration : IEntityTypeConfiguration<BillingPlanPriceCorrection>
{
    public void Configure(EntityTypeBuilder<BillingPlanPriceCorrection> builder)
    {
        builder.ToTable("BillingPlanPriceCorrections", table =>
        {
            table.HasCheckConstraint("CK_BillingPlanPriceCorrections_PreviousAmount", "[PreviousAmount] > 0");
            table.HasCheckConstraint("CK_BillingPlanPriceCorrections_NewAmount", "[NewAmount] > 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.PreviousAmount).HasPrecision(18, 2);
        builder.Property(x => x.NewAmount).HasPrecision(18, 2);
        builder.Property(x => x.PreviousCurrency).IsRequired().HasMaxLength(BillingPrice.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.NewCurrency).IsRequired().HasMaxLength(BillingPrice.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Ignore(x => x.PreviousPrice);
        builder.Ignore(x => x.NewPrice);

        foreach (var property in builder.Metadata.GetProperties().Where(p => !p.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasIndex(x => new { x.PlanPriceId, x.CorrectedAtUtc })
            .HasDatabaseName("IX_BillingPlanPriceCorrections_PlanPriceId_CorrectedAtUtc");
    }
}
