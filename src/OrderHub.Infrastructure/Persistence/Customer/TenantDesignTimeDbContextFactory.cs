using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OrderHub.Infrastructure.Persistence.Customer;

/// <summary>
/// Only used for scaffolding TenantDbContext migrations. At runtime, real
/// customer connections are resolved per-tenant by ITenantDbContextFactory.
/// This factory points at a design-time scratch database whose only purpose
/// is to generate migration files. It is never queried by the API or Worker.
/// </summary>
public sealed class TenantDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var solutionRoot = FindSolutionRoot();
        var apiAppSettings = Path.Combine(solutionRoot, "src", "OrderHub.Api", "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(solutionRoot)
            .AddJsonFile(apiAppSettings, optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Use dedicated key if provided, otherwise derive from CentralDb's server only.
        var cs = configuration.GetConnectionString("CustomerDbDesignTime")
                 ?? BuildFromCentral(configuration)
                 ?? throw new InvalidOperationException(
                     "CustomerDb design-time connection string not found. " +
                     "Set 'ConnectionStrings:CustomerDbDesignTime' in appsettings.json, " +
                     "or ensure 'ConnectionStrings:CentralDb' is set so a design-time DB name can be derived.");

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(cs)
            .Options;

        return new TenantDbContext(options);
    }

    private static string? BuildFromCentral(IConfiguration configuration)
    {
        var central = configuration.GetConnectionString("CentralDb");
        if (string.IsNullOrWhiteSpace(central)) return null;

        // Replace the Database= portion with the design-time scratch DB name.
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(central)
        {
            InitialCatalog = "OrderHub_Customer_DesignTime"
        };
        return builder.ConnectionString;
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !dir.GetFiles("*.sln").Any())
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException(
                "Could not locate solution root (no .sln file found in any parent directory of " +
                Directory.GetCurrentDirectory() + ").");
        }

        return dir.FullName;
    }
}