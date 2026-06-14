using Microsoft.EntityFrameworkCore;
using CentralTenant = OrderHub.Domain.Entities.Central.Tenant;
using CentralAdminUser = OrderHub.Domain.Entities.Central.CentralAdminUser;
using TenantMembership = OrderHub.Domain.Entities.Central.TenantMembership;
using PendingRegistration = OrderHub.Domain.Entities.Central.PendingRegistration;
using PrintBridgeDevice = OrderHub.Domain.Entities.Central.PrintBridgeDevice;
using BusinessType = OrderHub.Domain.Entities.Central.BusinessType;
using Country = OrderHub.Domain.Entities.Central.Country;
using City = OrderHub.Domain.Entities.Central.City;
using District = OrderHub.Domain.Entities.Central.District;
using Neighborhood = OrderHub.Domain.Entities.Central.Neighborhood;
using Street = OrderHub.Domain.Entities.Central.Street;
using PendingRegistrationBusinessType = OrderHub.Domain.Entities.Central.PendingRegistrationBusinessType;
using OrderHub.Infrastructure.Persistence.Central.Configurations;

namespace OrderHub.Infrastructure.Persistence.Central;

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
        modelBuilder.ApplyConfiguration(new PendingRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessTypeConfiguration());
        modelBuilder.ApplyConfiguration(new CountryConfiguration());
        modelBuilder.ApplyConfiguration(new CityConfiguration());
        modelBuilder.ApplyConfiguration(new DistrictConfiguration());
        modelBuilder.ApplyConfiguration(new NeighborhoodConfiguration());
        modelBuilder.ApplyConfiguration(new StreetConfiguration());
        modelBuilder.ApplyConfiguration(new PendingRegistrationBusinessTypeConfiguration());
    }
}
