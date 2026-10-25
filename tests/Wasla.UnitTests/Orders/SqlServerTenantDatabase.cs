using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// A throwaway tenant database on a real SQL Server for concurrency tests, migrated with this build's tenant
/// migrations. The server is <c>WASLA_TEST_SQLSERVER</c> (for example <c>(localdb)\WaslaTests</c>), otherwise
/// <c>(localdb)\MSSQLLocalDB</c>, which the Windows CI runner provides. The database name is generated here and only a
/// database with that generated prefix is ever dropped, so no existing database is touched.
/// </summary>
public sealed class SqlServerTenantDatabase : IAsyncLifetime
{
    public const string ServerVariable = "WASLA_TEST_SQLSERVER";
    private const string DefaultServer = @"(localdb)\MSSQLLocalDB";
    private const string NamePrefix = "Wasla_ConcurrencyTest_";
    private static readonly Regex SafeServer = new(@"^[A-Za-z0-9_.\\()\-,]+$", RegexOptions.CultureInvariant);

    public string Server { get; } = ResolveServer();
    public string DatabaseName { get; } = NamePrefix + Guid.NewGuid().ToString("N")[..12];

    internal static string ResolveServer()
    {
        var configured = Environment.GetEnvironmentVariable(ServerVariable);
        var server = string.IsNullOrWhiteSpace(configured) ? DefaultServer : configured.Trim();
        if (!SafeServer.IsMatch(server))
            throw new InvalidOperationException($"{ServerVariable} must name a SQL Server instance only, not a connection string.");
        return server;
    }

    /// <summary>
    /// A connection string for one simulated application instance. Each distinct <paramref name="instance"/> gets its
    /// own connection pool (Application Name is part of the pool key), as separate Wasla processes would.
    /// </summary>
    public string ConnectionString(string instance) => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = DatabaseName,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ApplicationName = "Wasla.ConcurrencyTests." + instance,
        MaxPoolSize = 32,
        ConnectTimeout = 60
    }.ConnectionString;

    private string MasterConnectionString() => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = "master",
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ApplicationName = "Wasla.ConcurrencyTests.Setup",
        ConnectTimeout = 60
    }.ConnectionString;

    /// <summary>Tenant database contexts for one simulated application instance.</summary>
    public ITenantDbContextFactory Instance(string instance) => new InstanceFactory(ConnectionString(instance));

    public TenantDbContext Open(string instance = "Inspector") =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(ConnectionString(instance)).Options);

    public async ValueTask InitializeAsync()
    {
        await using (var master = new SqlConnection(MasterConnectionString()))
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

        await using var master = new SqlConnection(MasterConnectionString());
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class InstanceFactory(string connectionString) : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct) =>
            Task.FromResult(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connectionString).Options));
    }
}
