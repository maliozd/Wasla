using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Customer;

namespace Wasla.Infrastructure.Persistence.Tenant.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.ExternalOrderId).IsRequired().HasMaxLength(128);
        builder.Property(o => o.ExternalOrderCode).HasMaxLength(128);
        builder.Property(o => o.IdempotencyKey).IsRequired().HasMaxLength(88); // base64 of sha256
        builder.Property(o => o.PlatformStatus).HasMaxLength(64);

        builder.Property(o => o.CustomerName).HasMaxLength(200);
        builder.Property(o => o.CustomerPhone).HasMaxLength(50);
        builder.Property(o => o.CustomerAddress).HasMaxLength(1000);
        builder.Property(o => o.CustomerNote).HasMaxLength(2000);

        builder.Property(o => o.TotalAmount).HasPrecision(18, 2);
        builder.Property(o => o.DeliveryFee).HasPrecision(18, 2);
        builder.Property(o => o.ServiceFee).HasPrecision(18, 2);

        // RawPayloadJson can be large - no max length constraint, let it be nvarchar(max).
        builder.Property(o => o.RawPayloadJson).HasColumnType("nvarchar(max)");

        builder.HasMany(o => o.Items)
            .WithOne(i => i.Order!)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // --- Indexes ---

        // Idempotency: unique. This is THE index that makes upsert safe.
        builder.HasIndex(o => o.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("IX_Orders_IdempotencyKey");

        // Also useful for admin lookup by (platform, external id).
        builder.HasIndex(o => new { o.Platform, o.ExternalOrderId })
            .IsUnique()
            .HasDatabaseName("IX_Orders_Platform_ExternalOrderId");

        // Dashboard / list view: "recent orders"
        builder.HasIndex(o => o.ReceivedAt)
            .IsDescending()
            .HasDatabaseName("IX_Orders_ReceivedAt");

        // Filter by status + recency
        builder.HasIndex(o => new { o.InternalStatus, o.ReceivedAt })
            .HasDatabaseName("IX_Orders_InternalStatus_ReceivedAt");
    }
}
