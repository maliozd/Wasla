using Microsoft.EntityFrameworkCore;
using CentralCustomer = OrderHub.Domain.Entities.Central.Customer;
using CentralAdminUser = OrderHub.Domain.Entities.Central.CentralAdminUser;
using CustomerMembership = OrderHub.Domain.Entities.Central.CustomerMembership;
using PendingRegistration = OrderHub.Domain.Entities.Central.PendingRegistration;
using PrintBridgeDevice = OrderHub.Domain.Entities.Central.PrintBridgeDevice;
using BusinessType = OrderHub.Domain.Entities.Central.BusinessType;
using City = OrderHub.Domain.Entities.Central.City;
using District = OrderHub.Domain.Entities.Central.District;
using PendingRegistrationBusinessType = OrderHub.Domain.Entities.Central.PendingRegistrationBusinessType;
using OrderHub.Infrastructure.Persistence.Central.Configurations;

namespace OrderHub.Infrastructure.Persistence.Central;

/// <summary>
/// Central database context. Holds the customer registry and nothing else.
/// This DB is reached through a static, well-known connection string from config.
/// </summary>
public class CentralDbContext : DbContext
{
    public CentralDbContext(DbContextOptions<CentralDbContext> options) : base(options) { }
    public DbSet<CentralCustomer> Customers => Set<CentralCustomer>();
    public DbSet<CustomerMembership> CustomerMemberships => Set<CustomerMembership>();
    public DbSet<CentralAdminUser> CentralAdminUsers => Set<CentralAdminUser>();
    public DbSet<PrintBridgeDevice> PrintBridgeDevices => Set<PrintBridgeDevice>();
    public DbSet<PendingRegistration> PendingRegistrations => Set<PendingRegistration>();
    public DbSet<BusinessType> BusinessTypes => Set<BusinessType>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<PendingRegistrationBusinessType> PendingRegistrationBusinessTypes => Set<PendingRegistrationBusinessType>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CustomerConfiguration());
        modelBuilder.ApplyConfiguration(new CustomerMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new CentralAdminUserConfiguration());
        modelBuilder.ApplyConfiguration(new PrintBridgeDeviceConfiguration());
        modelBuilder.ApplyConfiguration(new PendingRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessTypeConfiguration());
        modelBuilder.ApplyConfiguration(new CityConfiguration());
        modelBuilder.ApplyConfiguration(new DistrictConfiguration());
        modelBuilder.ApplyConfiguration(new PendingRegistrationBusinessTypeConfiguration());
    }
}
