using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public sealed class PrintBridgeDeviceConfiguration : IEntityTypeConfiguration<PrintBridgeDevice>
{
    public void Configure(EntityTypeBuilder<PrintBridgeDevice> builder)
    {
        builder.ToTable("PrintBridgeDevices");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(88);
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.MachineName).HasMaxLength(200);
        builder.Property(x => x.PrinterName).HasMaxLength(200);
        builder.Property(x => x.AppVersion).HasMaxLength(50);
        builder.Property(x => x.LastIpAddress).HasMaxLength(64);
        builder.Property(x => x.RemovedAtUtc);

        builder.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("IX_PrintBridgeDevices_TokenHash");

        builder.HasIndex(x => x.TenantId)
            .HasDatabaseName("IX_PrintBridgeDevices_TenantId");

        builder.HasIndex(x => new { x.TenantId, x.RemovedAtUtc })
            .HasDatabaseName("IX_PrintBridgeDevices_TenantId_RemovedAtUtc");

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
