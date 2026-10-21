using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Orders;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// A throwaway CentralDb on a real SQL Server, migrated with this build's central migrations (WAS-147). The server is
/// the one <see cref="SqlServerTenantDatabase"/> uses (<c>WASLA_TEST_SQLSERVER</c>, otherwise
/// <c>(localdb)\MSSQLLocalDB</c>). Only a database with the generated <c>Wasla_BillingTest_</c> prefix is ever dropped.
/// </summary>
public sealed class SqlServerCentralDatabase : IAsyncLifetime
{
    private const string NamePrefix = "Wasla_BillingTest_";

    private readonly string _server = SqlServerTenantDatabase.ResolveServer();

    public string DatabaseName { get; } = NamePrefix + Guid.NewGuid().ToString("N")[..12];

    public BillingClock Time { get; } = new(BillingTestData.Now);

    /// <summary>Each distinct <paramref name="instance"/> has its own connection pool, as separate processes would.</summary>
    public CentralDbContext Open(string instance = "Inspector") => new(new DbContextOptionsBuilder<CentralDbContext>()
        .UseSqlServer(ConnectionString(DatabaseName, instance))
        .Options);

    public BillingSubscriptionStore Store(CentralDbContext db) => new(db, Time, NullLogger<BillingSubscriptionStore>.Instance);

    public async ValueTask InitializeAsync()
    {
        await using (var master = new SqlConnection(ConnectionString("master", "Setup")))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{DatabaseName}];";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = Open("Setup");
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        if (!DatabaseName.StartsWith(NamePrefix, StringComparison.Ordinal))
            return;

        await using var master = new SqlConnection(ConnectionString("master", "Setup"));
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    private string ConnectionString(string database, string instance) => new SqlConnectionStringBuilder
    {
        DataSource = _server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ApplicationName = "Wasla.BillingTests." + instance,
        MaxPoolSize = 32,
        ConnectTimeout = 60
    }.ConnectionString;
}
