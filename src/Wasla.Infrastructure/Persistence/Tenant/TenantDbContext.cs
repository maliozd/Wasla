using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;

namespace Wasla.Infrastructure.Persistence.Tenant;

/// <summary>
/// Per-customer database context. One physical database per restaurant.
/// The connection string is resolved per-request/per-sync from CentralDb
/// and passed in via ITenantDbContextFactory.
/// </summary>
public class TenantDbContext : DbContext
{
    public TenantDbContext(DbContextOptions<TenantDbContext> options) : base(options) { }

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<UserNotificationSettings> UserNotificationSettings => Set<UserNotificationSettings>();
    public DbSet<UserProductTourCompletion> UserProductTourCompletions => Set<UserProductTourCompletion>();
    public DbSet<GuidedDemoSession> GuidedDemoSessions => Set<GuidedDemoSession>();
    public DbSet<UserGuidedSetupState> UserGuidedSetupStates => Set<UserGuidedSetupState>();
    public DbSet<TenantOperationalSettings> TenantOperationalSettings => Set<TenantOperationalSettings>();
    public DbSet<PlatformConnection> PlatformConnections => Set<PlatformConnection>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemOption> OrderItemOptions => Set<OrderItemOption>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<IntegrationError> IntegrationErrors => Set<IntegrationError>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new BranchConfiguration());
        modelBuilder.ApplyConfiguration(new AppUserConfiguration());
        modelBuilder.ApplyConfiguration(new PasswordResetTokenConfiguration());
        modelBuilder.ApplyConfiguration(new UserNotificationSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new UserProductTourCompletionConfiguration());
        modelBuilder.ApplyConfiguration(new GuidedDemoSessionConfiguration());
        modelBuilder.ApplyConfiguration(new UserGuidedSetupStateConfiguration());
        modelBuilder.ApplyConfiguration(new TenantOperationalSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new PlatformConnectionConfiguration());
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new OrderItemConfiguration());
        modelBuilder.ApplyConfiguration(new OrderItemOptionConfiguration());
        modelBuilder.ApplyConfiguration(new SyncLogConfiguration());
        modelBuilder.ApplyConfiguration(new IntegrationErrorConfiguration());
        modelBuilder.ApplyConfiguration(new PrintJobConfiguration());
    }

    /// <summary>
    /// Override SaveChanges to auto-update UpdatedAt on modified BaseEntity rows.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Domain.Common.BaseEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
