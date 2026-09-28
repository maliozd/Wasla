using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wasla.Domain.Entities.Customer;

namespace Wasla.Infrastructure.Persistence.Tenant.Configurations;

public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).IsRequired().HasMaxLength(200);
        builder.Property(b => b.Address).HasMaxLength(1000);
    }
}

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("AppUsers");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).IsRequired().HasMaxLength(255);
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(255);
        builder.Property(u => u.FullName).HasMaxLength(200);

        // Email must be unique within a customer DB.
        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("IX_AppUsers_Email");
        builder.HasIndex(u => u.BranchId).HasDatabaseName("IX_AppUsers_BranchId");
    }
}

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetTokens");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(88);
        builder.Property(x => x.ExpiresAtUtc).IsRequired();
        builder.Property(x => x.UsedAtUtc);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        builder.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("IX_PasswordResetTokens_TokenHash");

        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("IX_PasswordResetTokens_UserId");

        builder.HasIndex(x => x.ExpiresAtUtc)
            .HasDatabaseName("IX_PasswordResetTokens_ExpiresAtUtc");
    }
}

public class GuidedDemoSessionConfiguration : IEntityTypeConfiguration<GuidedDemoSession>
{
    public void Configure(EntityTypeBuilder<GuidedDemoSession> builder)
    {
        builder.ToTable("GuidedDemoSessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.UserId).IsRequired();
        builder.Property(session => session.ScenarioCode).IsRequired().HasMaxLength(64);
        builder.Property(session => session.CustomerNameKey).IsRequired().HasMaxLength(128);
        builder.Property(session => session.NoteKey).HasMaxLength(128);
        builder.Property(session => session.ItemsJson).IsRequired();
        builder.Property(session => session.ReceivedAtUtc).IsRequired();
        builder.Property(session => session.ExpiresAtUtc).IsRequired();

        builder.HasOne(session => session.User)
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        builder.HasIndex(session => new { session.UserId, session.CompletedAtUtc })
            .HasDatabaseName("IX_GuidedDemoSessions_UserId_CompletedAtUtc");
    }
}

public class UserProductTourCompletionConfiguration : IEntityTypeConfiguration<UserProductTourCompletion>
{
    public void Configure(EntityTypeBuilder<UserProductTourCompletion> builder)
    {
        builder.ToTable("UserProductTourCompletions");
        builder.HasKey(completion => completion.Id);
        builder.Property(completion => completion.UserId).IsRequired();
        builder.Property(completion => completion.TourKey).IsRequired().HasMaxLength(64);
        builder.Property(completion => completion.CompletedAtUtc).IsRequired();

        builder.HasOne(completion => completion.User)
            .WithMany()
            .HasForeignKey(completion => completion.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        builder.HasIndex(completion => new { completion.UserId, completion.TourKey })
            .IsUnique()
            .HasDatabaseName("IX_UserProductTourCompletions_UserId_TourKey");
    }
}

public class UserNotificationSettingsConfiguration : IEntityTypeConfiguration<UserNotificationSettings>
{
    public void Configure(EntityTypeBuilder<UserNotificationSettings> builder)
    {
        builder.ToTable("UserNotificationSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.UserId).IsRequired();
        builder.Property(s => s.NewOrderSoundEnabled).IsRequired();
        builder.Property(s => s.NewOrderSoundName).IsRequired().HasMaxLength(32);
        builder.Property(s => s.NewOrderSoundRepeatCount).IsRequired();
        builder.Property(s => s.NewOrderSoundVolume).HasPrecision(4, 2);
        builder.Property(s => s.ShowBrowserNotification).IsRequired();
        builder.Property(s => s.NewOrderHighlightColor).IsRequired().HasMaxLength(32);
        builder.Property(s => s.NewOrderHighlightBehavior).IsRequired().HasMaxLength(32);
        builder.Property(s => s.NewOrderHighlightDurationSeconds).IsRequired();

        // One settings row per AppUser.
        builder.HasIndex(s => s.UserId)
            .IsUnique()
            .HasDatabaseName("IX_UserNotificationSettings_UserId");

        builder.HasOne(s => s.AppUser)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class TenantOperationalSettingsConfiguration : IEntityTypeConfiguration<TenantOperationalSettings>
{
    public void Configure(EntityTypeBuilder<TenantOperationalSettings> builder)
    {
        builder.ToTable("TenantOperationalSettings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderSyncEnabled)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(x => x.AutoApproveNewOrders)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(x => x.AutoPrintReceiptOnAutoApprove)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(x => x.ReceiptPrintCopyCount)
            .IsRequired()
            .HasDefaultValue(1);

        builder.Property(x => x.ReceiptTemplateSettingsJson)
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.SetupGuidanceCompletedAtUtc);

        // Single-row table pattern (enforced by always updating a known row id in the service).
        builder.HasIndex(x => x.Id)
            .IsUnique()
            .HasDatabaseName("IX_TenantOperationalSettings_Id");
    }
}

public class PrintJobConfiguration : IEntityTypeConfiguration<PrintJob>
{
    public void Configure(EntityTypeBuilder<PrintJob> builder)
    {
        builder.ToTable("PrintJobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Type).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CopyCount).IsRequired();
        builder.Property(x => x.PrinterName).HasMaxLength(200);
        builder.Property(x => x.PayloadJson).IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(1000);
        builder.Property(x => x.LockedByInstallationId);
        builder.Property(x => x.LockedBy).HasMaxLength(200);

        builder.HasIndex(x => new { x.OrderId, x.Type })
            .HasDatabaseName("IX_PrintJobs_OrderId_Type");

        builder.HasIndex(x => new { x.Status, x.LockedByInstallationId })
            .HasDatabaseName("IX_PrintJobs_Status_LockedByInstallationId");

        builder.HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlatformConnectionConfiguration : IEntityTypeConfiguration<PlatformConnection>
{
    public void Configure(EntityTypeBuilder<PlatformConnection> builder)
    {
        builder.ToTable("PlatformConnections");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.StoreId).HasMaxLength(128);
        builder.Property(p => p.SupplierId).HasMaxLength(64);
        builder.Property(p => p.ExecutorEmail).HasMaxLength(256);
        builder.Property(p => p.EncryptedApiKey).HasMaxLength(2000);
        builder.Property(p => p.EncryptedApiSecret).HasMaxLength(2000);

        // One connection per platform. StoreId is provider configuration, not identity.
        builder.HasIndex(p => p.Platform)
            .IsUnique()
            .HasDatabaseName("IX_PlatformConnections_Platform");

        builder.HasIndex(p => p.IsActive).HasDatabaseName("IX_PlatformConnections_IsActive");
        builder.HasIndex(p => p.LastSuccessfulSync).HasDatabaseName("IX_PlatformConnections_LastSuccessfulSync");
        builder.HasIndex(p => p.CircuitOpenUntil).HasDatabaseName("IX_PlatformConnections_CircuitOpenUntil");
    }
}

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.ProductName).IsRequired().HasMaxLength(300);
        builder.Property(i => i.UnitPrice).HasPrecision(18, 2);
        builder.Property(i => i.TotalPrice).HasPrecision(18, 2);
        builder.Property(i => i.Notes).HasMaxLength(1000);

        builder.HasMany(i => i.Options)
            .WithOne(o => o.OrderItem!)
            .HasForeignKey(o => o.OrderItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => i.OrderId).HasDatabaseName("IX_OrderItems_OrderId");
    }
}

public class OrderItemOptionConfiguration : IEntityTypeConfiguration<OrderItemOption>
{
    public void Configure(EntityTypeBuilder<OrderItemOption> builder)
    {
        builder.ToTable("OrderItemOptions");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Name).IsRequired().HasMaxLength(200);
        builder.Property(o => o.Price).HasPrecision(18, 2);
        builder.HasIndex(o => o.OrderItemId).HasDatabaseName("IX_OrderItemOptions_OrderItemId");
    }
}

public class SyncLogConfiguration : IEntityTypeConfiguration<SyncLog>
{
    public void Configure(EntityTypeBuilder<SyncLog> builder)
    {
        builder.ToTable("SyncLogs");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ErrorMessage).HasMaxLength(2000);
        builder.HasIndex(s => s.PlatformConnectionId).HasDatabaseName("IX_SyncLogs_PlatformConnectionId");
        builder.HasIndex(s => s.StartedAt).IsDescending().HasDatabaseName("IX_SyncLogs_StartedAt");
    }
}

public class IntegrationErrorConfiguration : IEntityTypeConfiguration<IntegrationError>
{
    public void Configure(EntityTypeBuilder<IntegrationError> builder)
    {
        builder.ToTable("IntegrationErrors");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ErrorType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ErrorMessage).HasMaxLength(2000);
        builder.HasIndex(e => e.IsResolved).HasDatabaseName("IX_IntegrationErrors_IsResolved");
        builder.HasIndex(e => e.PlatformConnectionId).HasDatabaseName("IX_IntegrationErrors_PlatformConnectionId");
    }
}
