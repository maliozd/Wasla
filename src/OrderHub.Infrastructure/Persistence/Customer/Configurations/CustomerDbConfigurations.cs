using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderHub.Domain.Entities.Customer;

namespace OrderHub.Infrastructure.Persistence.Customer.Configurations;

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

        // One row per external store: duplicate Platform + StoreId is blocked.
        // Same platform with a different StoreId is allowed (multiple stores).
        builder.HasIndex(p => new { p.Platform, p.StoreId })
            .IsUnique()
            .HasDatabaseName("IX_PlatformConnections_Platform_StoreId");

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
