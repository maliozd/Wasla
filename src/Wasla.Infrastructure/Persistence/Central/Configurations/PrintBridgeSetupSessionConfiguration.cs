using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Persistence.Central.Configurations;

public sealed class PrintBridgeSetupSessionConfiguration : IEntityTypeConfiguration<PrintBridgeSetupSession>
{
    public void Configure(EntityTypeBuilder<PrintBridgeSetupSession> builder)
    {
        builder.ToTable("PrintBridgeSetupSessions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CodeHash).IsRequired().HasMaxLength(88);
        builder.Property(x => x.CompletionCredentialHash).HasMaxLength(88);
        builder.Property(x => x.SetupMode).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ServerUrl).IsRequired().HasMaxLength(500);
        builder.Property(x => x.ExpiresAtUtc).IsRequired();
        builder.Property(x => x.ConnectionVerified).IsRequired();
        builder.Property(x => x.FailureReason).HasMaxLength(300);

        builder.HasIndex(x => x.CodeHash)
            .IsUnique()
            .HasDatabaseName("IX_PrintBridgeSetupSessions_CodeHash");

        builder.HasIndex(x => x.TenantId)
            .HasDatabaseName("IX_PrintBridgeSetupSessions_TenantId");

        builder.HasIndex(x => x.ExpiresAtUtc)
            .HasDatabaseName("IX_PrintBridgeSetupSessions_ExpiresAtUtc");

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // PrintBridgeDeviceId is nullable: NewDevice sessions bind the device atomically at exchange time.
        // Tenant is the single cascade path. Device FK uses NoAction to avoid
        // SQL Server "multiple cascade paths" (Tenant -> Device -> Session and Tenant -> Session).
        builder.HasOne(x => x.Device)
            .WithMany()
            .HasForeignKey(x => x.PrintBridgeDeviceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
