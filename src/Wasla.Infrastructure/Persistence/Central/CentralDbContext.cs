using Microsoft.EntityFrameworkCore;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;
using CentralAdminUser = Wasla.Domain.Entities.Central.CentralAdminUser;
using TenantMembership = Wasla.Domain.Entities.Central.TenantMembership;
using PendingRegistration = Wasla.Domain.Entities.Central.PendingRegistration;
using PrintBridgeDevice = Wasla.Domain.Entities.Central.PrintBridgeDevice;
using PrintBridgeSetupSession = Wasla.Domain.Entities.Central.PrintBridgeSetupSession;
using BusinessType = Wasla.Domain.Entities.Central.BusinessType;
using Country = Wasla.Domain.Entities.Central.Country;
using City = Wasla.Domain.Entities.Central.City;
using District = Wasla.Domain.Entities.Central.District;
using Neighborhood = Wasla.Domain.Entities.Central.Neighborhood;
using Street = Wasla.Domain.Entities.Central.Street;
using PendingRegistrationBusinessType = Wasla.Domain.Entities.Central.PendingRegistrationBusinessType;
using Wasla.Infrastructure.Persistence.Central.Configurations;

namespace Wasla.Infrastructure.Persistence.Central;

/// <summary>
/// Central database context. Holds the tenant registry and nothing else.
/// This DB is reached through a static, well-known connection string from config.
/// </summary>
public class CentralDbContext : DbContext
{
    public CentralDbContext(DbContextOptions<CentralDbContext> options) : base(options) { }
    public DbSet<CentralTenant> Tenants => Set<CentralTenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<CentralAdminUser> CentralAdminUsers => Set<CentralAdminUser>();
    public DbSet<PrintBridgeDevice> PrintBridgeDevices => Set<PrintBridgeDevice>();
    public DbSet<PrintBridgeSetupSession> PrintBridgeSetupSessions => Set<PrintBridgeSetupSession>();
    public DbSet<PendingRegistration> PendingRegistrations => Set<PendingRegistration>();
    public DbSet<BusinessType> BusinessTypes => Set<BusinessType>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<Neighborhood> Neighborhoods => Set<Neighborhood>();
    public DbSet<Street> Streets => Set<Street>();
    public DbSet<PendingRegistrationBusinessType> PendingRegistrationBusinessTypes => Set<PendingRegistrationBusinessType>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TenantConfiguration());
        modelBuilder.ApplyConfiguration(new TenantMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new CentralAdminUserConfiguration());
        modelBuilder.ApplyConfiguration(new PrintBridgeDeviceConfiguration());
        modelBuilder.ApplyConfiguration(new PrintBridgeSetupSessionConfiguration());
        modelBuilder.ApplyConfiguration(new PendingRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessTypeConfiguration());
        modelBuilder.ApplyConfiguration(new CountryConfiguration());
        modelBuilder.ApplyConfiguration(new CityConfiguration());
        modelBuilder.ApplyConfiguration(new DistrictConfiguration());
        modelBuilder.ApplyConfiguration(new NeighborhoodConfiguration());
        modelBuilder.ApplyConfiguration(new StreetConfiguration());
        modelBuilder.ApplyConfiguration(new PendingRegistrationBusinessTypeConfiguration());
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RotateCentralAdminSecurityStamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RotateCentralAdminSecurityStamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// A password change or an activation change saved through SaveChanges revokes every session issued before it,
    /// whichever service saves it. Re-enabling therefore cannot revive a cookie revoked by the deactivation. A save that
    /// sets the stamp itself keeps it. ExecuteUpdate and raw SQL never pass through here and must set the stamp themselves.
    /// </summary>
    private void RotateCentralAdminSecurityStamps()
    {
        foreach (var entry in ChangeTracker.Entries<CentralAdminUser>())
        {
            if (entry.State != EntityState.Modified)
                continue;

            // Update() on a detached entity marks every property modified: that is treated as a credential change, and
            // its unchanged stamp is not treated as one the caller chose.
            var credentialsChanged = entry.Property(x => x.PasswordHash).IsModified || entry.Property(x => x.IsActive).IsModified;
            var stamp = entry.Property(x => x.SecurityStamp);
            var stampSetByCaller = stamp.IsModified && stamp.CurrentValue != stamp.OriginalValue;
            if (credentialsChanged && !stampSetByCaller)
                stamp.CurrentValue = Guid.NewGuid();
        }
    }
}
