using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("TenantMemberships");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.PlanCode).IsRequired().HasMaxLength(50);
        builder.Property(m => m.BillingPeriod).IsRequired().HasMaxLength(20);
        builder.Property(m => m.OwnerEmail).HasMaxLength(256);
        builder.Property(m => m.BusinessPhone).HasMaxLength(50);
        builder.Property(m => m.City).HasMaxLength(100);
        builder.Property(m => m.Country).HasMaxLength(100);
        builder.Property(m => m.BusinessType).HasMaxLength(100);

        builder.HasIndex(m => m.TenantId).IsUnique().HasDatabaseName("IX_TenantMemberships_TenantId");

        builder.HasOne(m => m.Tenant)
            .WithOne()
            .HasForeignKey<TenantMembership>(m => m.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
