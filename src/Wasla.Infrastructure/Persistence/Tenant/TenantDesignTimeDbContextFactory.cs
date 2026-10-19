using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Wasla.Infrastructure.Persistence.Tenant;

/// <summary>
/// Only used for scaffolding TenantDbContext migrations. At runtime, tenant
/// connections are resolved per-tenant by ITenantDbContextFactory.
/// This factory points at a design-time scratch database whose only purpose
/// is to generate migration files. It is never queried by the API or Worker.
/// </summary>
public sealed class TenantDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    internal const string ConnectionStringName = "TenantDbDesignTime";
    internal const string DerivedCatalog = "Wasla_Tenant_DesignTime";

    public TenantDbContext CreateDbContext(string[] args)
    {
        var solutionRoot = FindSolutionRoot();
        var apiAppSettings = Path.Combine(solutionRoot, "src", "Wasla.Api", "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(solutionRoot)
            .AddJsonFile(apiAppSettings, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(ResolveConnectionString(configuration))
            .Options;

        return new TenantDbContext(options);
    }

    /// <summary>
    /// <c>ConnectionStrings:TenantDbDesignTime</c> if set, otherwise CentralDb's server with the scratch catalog
    /// <c>Wasla_Tenant_DesignTime</c>.
    /// </summary>
    internal static string ResolveConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? BuildFromCentral(configuration)
        ?? throw new InvalidOperationException(
            "TenantDb design-time connection string not found. " +
            $"Set 'ConnectionStrings:{ConnectionStringName}' in appsettings.json, " +
            "or ensure 'ConnectionStrings:CentralDb' is set so a design-time DB name can be derived.");

    private static string? BuildFromCentral(IConfiguration configuration)
    {
        var central = configuration.GetConnectionString("CentralDb");
        if (string.IsNullOrWhiteSpace(central)) return null;

        // Replace the Database= portion with the design-time scratch DB name.
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(central)
        {
            InitialCatalog = DerivedCatalog
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