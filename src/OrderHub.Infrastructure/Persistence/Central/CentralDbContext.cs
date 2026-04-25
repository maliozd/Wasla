using Microsoft.EntityFrameworkCore;
using CentralCustomer = OrderHub.Domain.Entities.Central.Customer;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CustomerConfiguration());
    }
}
