using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CentralCustomer = OrderHub.Domain.Entities.Central.Customer;

namespace OrderHub.Infrastructure.Persistence.Central.Configurations;

public class CustomerConfiguration : IEntityTypeConfiguration<CentralCustomer>
{
    public void Configure(EntityTypeBuilder<CentralCustomer> builder)
    {
        builder.ToTable("Customers");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
        builder.Property(c => c.Slug).IsRequired().HasMaxLength(100);
        builder.Property(c => c.PrimaryDomain).IsRequired().HasMaxLength(255);
        builder.Property(c => c.DatabaseName).IsRequired().HasMaxLength(128);
        builder.Property(c => c.EncryptedConnectionString).IsRequired().HasMaxLength(2000);
        builder.Property(c => c.SchemaVersion).IsRequired().HasMaxLength(20);
        builder.Property(c => c.LastMigrationResult).HasMaxLength(1000);
        builder.Property(c => c.BillingPaymentStatus).HasConversion<int>();
        builder.Property(c => c.ProvisioningStatus).HasConversion<int>();
        builder.Property(c => c.SubscriptionStatus).HasConversion<int>();

        // Primary lookup path for every single web request - must be indexed and unique.
        builder.HasIndex(c => c.PrimaryDomain).IsUnique().HasDatabaseName("IX_Customers_PrimaryDomain");

        builder.HasIndex(c => c.Slug).IsUnique().HasDatabaseName("IX_Customers_Slug");

        // For worker: "give me all active customers". Filtered index for efficiency.
        builder.HasIndex(c => c.IsActive).HasDatabaseName("IX_Customers_IsActive");
    }
}
