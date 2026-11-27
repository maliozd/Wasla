using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Enums;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<CentralTenant>
{
    public void Configure(EntityTypeBuilder<CentralTenant> builder)
    {
        builder.ToTable("Tenants");

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

        // WAS-235: the database default keeps every existing row, and every insert from a build that predates the
        // column, assigned to Orders. The sentinel is 0, which is not a product: EF sends every real value it holds,
        // and only an unset 0 would fall back to the default.
        builder.Property(c => c.Product)
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(TenantProduct.Orders)
            .HasSentinel((TenantProduct)0);

        // Primary lookup path for every single web request - must be indexed and unique.
        builder.HasIndex(c => c.PrimaryDomain).IsUnique().HasDatabaseName("IX_Tenants_PrimaryDomain");

        builder.HasIndex(c => c.Slug).IsUnique().HasDatabaseName("IX_Tenants_Slug");

        // One tenant per physical database (WAS-93): two rows naming the same database would let one tenant read,
        // reset or drop the other's data.
        builder.HasIndex(c => c.DatabaseName).IsUnique().HasDatabaseName("IX_Tenants_DatabaseName");

        // For worker: "give me all active tenants". Filtered index for efficiency.
        builder.HasIndex(c => c.IsActive).HasDatabaseName("IX_Tenants_IsActive");
    }
}
