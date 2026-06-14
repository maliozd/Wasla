using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderHub.Domain.Entities.Central;

namespace OrderHub.Infrastructure.Persistence.Central.Configurations;

public sealed class CustomerMembershipConfiguration : IEntityTypeConfiguration<CustomerMembership>
{
    public void Configure(EntityTypeBuilder<CustomerMembership> builder)
    {
        builder.ToTable("CustomerMemberships");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.PlanCode).IsRequired().HasMaxLength(50);
        builder.Property(m => m.BillingPeriod).IsRequired().HasMaxLength(20);
        builder.Property(m => m.OwnerEmail).HasMaxLength(256);
        builder.Property(m => m.BusinessPhone).HasMaxLength(50);
        builder.Property(m => m.City).HasMaxLength(100);
        builder.Property(m => m.Country).HasMaxLength(100);
        builder.Property(m => m.BusinessType).HasMaxLength(100);

        builder.HasIndex(m => m.CustomerId).IsUnique().HasDatabaseName("IX_CustomerMemberships_CustomerId");

        builder.HasOne(m => m.Customer)
            .WithOne()
            .HasForeignKey<CustomerMembership>(m => m.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
