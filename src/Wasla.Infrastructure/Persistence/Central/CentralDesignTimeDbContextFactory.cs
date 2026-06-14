using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Wasla.Infrastructure.Persistence.Central;

public sealed class CentralDesignTimeDbContextFactory : IDesignTimeDbContextFactory<CentralDbContext>
{
    public CentralDbContext CreateDbContext(string[] args)
    {
        var solutionRoot = FindSolutionRoot();
        var apiAppSettings = Path.Combine(solutionRoot, "src", "Wasla.Api", "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(solutionRoot)
            .AddJsonFile(apiAppSettings, optional: false)
            .AddEnvironmentVariables()
            .Build();

        var cs = configuration.GetConnectionString("CentralDb")
            ?? throw new InvalidOperationException(
                "CentralDb connection string not found. " +
                "Set it in src/Wasla.Api/appsettings.json under 'ConnectionStrings:CentralDb', " +
                "or via environment variable 'ConnectionStrings__CentralDb', " +
                "or pass --connection to the EF command.");

        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseSqlServer(cs)
            .Options;

        return new CentralDbContext(options);
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