using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderHub.Domain.Entities.Central;

namespace OrderHub.Infrastructure.Persistence.Central.Configurations;

public class PendingRegistrationConfiguration : IEntityTypeConfiguration<PendingRegistration>
{
    public void Configure(EntityTypeBuilder<PendingRegistration> builder)
    {
        builder.ToTable("PendingRegistrations");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.PlanCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.BillingPeriod).IsRequired().HasMaxLength(20);
        builder.Property(x => x.BusinessName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.BusinessType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Slug).IsRequired().HasMaxLength(100);
        builder.Property(x => x.PrimaryDomain).IsRequired().HasMaxLength(255);
        builder.Property(x => x.DatabaseName).IsRequired().HasMaxLength(128);
        builder.Property(x => x.BusinessPhone).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Country).IsRequired().HasMaxLength(100);
        builder.Property(x => x.City).IsRequired().HasMaxLength(100);
        builder.Property(x => x.District).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Neighborhood).HasMaxLength(100);
        builder.Property(x => x.AddressLine1).IsRequired().HasMaxLength(300);
        builder.Property(x => x.AddressLine2).HasMaxLength(300);
        builder.Property(x => x.PostalCode).HasMaxLength(20);
        builder.Property(x => x.OwnerFullName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.OwnerEmail).IsRequired().HasMaxLength(256);
        builder.Property(x => x.OwnerPhone).HasMaxLength(50);
        builder.Property(x => x.PasswordHash).IsRequired().HasMaxLength(500);
        builder.Property(x => x.SimulatedPaymentReference).HasMaxLength(100);

        builder.HasIndex(x => x.Slug).HasDatabaseName("IX_PendingRegistrations_Slug");
        builder.HasIndex(x => x.DatabaseName).HasDatabaseName("IX_PendingRegistrations_DatabaseName");
        builder.HasIndex(x => x.OwnerEmail).HasDatabaseName("IX_PendingRegistrations_OwnerEmail");
        builder.HasIndex(x => x.Status).HasDatabaseName("IX_PendingRegistrations_Status");
    }
}
