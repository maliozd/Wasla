using System.CommandLine;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Security;
using Wasla.Cli;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Tenant;
using Wasla.UnitTests.Billing;
using Wasla.UnitTests.Cli;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.UnitTests.Tenancy;

/// <summary>
/// WAS-235 on a real SQL Server CentralDb migrated with this build's migrations: the <c>Tenants.Product</c> column, its
/// database default and the tenant query the Worker and the CLI share.
/// </summary>
public sealed class TenantProductSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private const string Migration = "_AddTenantProduct";

    private readonly SqlServerCentralDatabase _db;

    public TenantProductSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Rows that exist before the migration, and rows a build without the column inserts afterwards, are Orders. Rolling
    /// the migration back and forward again drops every assignment back to Orders: a rollback past it is only safe
    /// while every tenant is on Orders (WAS-236).
    /// </summary>
    [Fact]
    public async Task ExistingRows_AndInsertsFromOlderBuilds_AreOrders_AndARollbackForgetsAssignments()
    {
        await using var db = _db.Open("Setup");
        var migrations = db.Database.GetMigrations().ToList();
        var added = migrations.Single(m => m.EndsWith(Migration, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(added) - 1];
        var migrator = db.GetService<IMigrator>();
        var before = Guid.NewGuid();
        var older = Guid.NewGuid();
        var pos = Guid.NewGuid();

        try
        {
            await migrator.MigrateAsync(previous, Ct);
            await InsertWithoutProductAsync(db, before);

            await migrator.MigrateAsync(null, Ct);
            await InsertWithoutProductAsync(db, older);
            db.Tenants.Add(Tenant(pos, TenantProduct.Pos));
            await db.SaveChangesAsync(Ct);

            Assert.Equal(TenantProduct.Orders, await ProductAsync(db, before));
            Assert.Equal(TenantProduct.Orders, await ProductAsync(db, older));
            Assert.Equal(TenantProduct.Pos, await ProductAsync(db, pos));

            await migrator.MigrateAsync(previous, Ct);
            await migrator.MigrateAsync(null, Ct);

            Assert.Equal(TenantProduct.Orders, await ProductAsync(db, pos));
        }
        finally
        {
            await migrator.MigrateAsync(null, Ct);
            await db.Tenants.Where(t => t.Id == before || t.Id == older || t.Id == pos).ExecuteDeleteAsync(Ct);
        }

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task TheSharedTenantQuery_SelectsActiveOrdersTenants_AndNamesTheOthers()
    {
        await using var db = _db.Open("Setup");
        var seeded = new Dictionary<string, CentralTenant>
        {
            ["orders"] = Tenant(Guid.NewGuid(), TenantProduct.Orders),
            ["inactive"] = Tenant(Guid.NewGuid(), TenantProduct.Orders, isActive: false),
            ["moving-to-pos"] = Tenant(Guid.NewGuid(), TenantProduct.MovingToPos),
            ["pos"] = Tenant(Guid.NewGuid(), TenantProduct.Pos),
            ["moving-to-orders"] = Tenant(Guid.NewGuid(), TenantProduct.MovingToOrders)
        };
        db.Tenants.AddRange(seeded.Values);
        await db.SaveChangesAsync(Ct);
        var ids = seeded.Values.Select(t => t.Id).ToList();

        try
        {
            var orders = await db.Tenants.AsNoTracking().ActiveOrdersTenants().Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(Ct);
            var others = await db.Tenants.AsNoTracking().ActiveTenantsNotOnOrders().Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(Ct);

            Assert.Equal([seeded["orders"].Id], orders);
            Assert.Equal(
                new[] { seeded["moving-to-pos"].Id, seeded["pos"].Id, seeded["moving-to-orders"].Id }.Order(),
                others.Order());
        }
        finally
        {
            await db.Tenants.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(Ct);
        }
    }

    /// <summary>A row as a build before WAS-235 writes it: every column it knows, no product.</summary>
    private static Task InsertWithoutProductAsync(CentralDbContext db, Guid id)
    {
        var slug = "old-" + id.ToString("N")[..12];
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [Tenants] ([Id], [Name], [Slug], [PrimaryDomain], [DatabaseName], [EncryptedConnectionString],
                [EncryptionKeyVersion], [SchemaVersion], [IsActive], [BillingPaymentStatus], [ProvisioningStatus],
                [SubscriptionStatus], [CreatedAt], [UpdatedAt])
            VALUES ({id}, {"Older build"}, {slug}, {slug + ".app.wasla.local"}, {"Wasla_test_" + id.ToString("N")}, {"unused"},
                1, {"1.0.0"}, 1, 0, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME())
            """,
            Ct);
    }

    private static Task<TenantProduct> ProductAsync(CentralDbContext db, Guid id) =>
        db.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => t.Product).SingleAsync(Ct);

    internal static CentralTenant Tenant(Guid id, TenantProduct product, bool isActive = true, string? slug = null)
    {
        slug ??= "t-" + id.ToString("N")[..12];
        return new CentralTenant
        {
            Id = id,
            Name = "Restaurant " + slug,
            Slug = slug,
            PrimaryDomain = slug + ".app.wasla.local",
            DatabaseName = "Wasla_test_" + id.ToString("N"),
            EncryptedConnectionString = "unused",
            IsActive = isActive,
            Product = product
        };
    }
}

/// <summary>
/// The migration gate that every Orders service runs before it starts (<c>migration-status --require-current</c>) on a
/// real SQL Server CentralDb (WAS-235): a tenant on POS, or between the products, has no Orders database to read and
/// does not stop the gate. Its own database, so no other test's tenants are in the registry. It captures the CLI's
/// console output, so it runs in <see cref="CliConsoleCollection"/>.
/// </summary>
[Collection(nameof(CliConsoleCollection))]
public sealed class TenantProductMigrationGateSqlServerTests : IClassFixture<SqlServerCentralDatabase>
{
    private readonly SqlServerCentralDatabase _db;

    public TenantProductMigrationGateSqlServerTests(SqlServerCentralDatabase db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheGate_PassesWithTenantsNotOnOrders_ThatHaveNoReadableOrdersDatabase()
    {
        string connectionString;
        await using (var db = _db.Open("Setup"))
        {
            db.Tenants.Add(TenantProductSqlServerTests.Tenant(Guid.NewGuid(), TenantProduct.Pos, slug: "gate-pos"));
            db.Tenants.Add(TenantProductSqlServerTests.Tenant(Guid.NewGuid(), TenantProduct.MovingToOrders, slug: "gate-moving"));
            await db.SaveChangesAsync(Ct);
            connectionString = new SqlConnectionStringBuilder(db.Database.GetConnectionString())
            {
                ApplicationName = "Wasla.TenantProductGateTests"
            }.ConnectionString;
        }

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<CentralDbContext>(options => options.UseSqlServer(connectionString));
                services.AddSingleton<ISecretManager, NoTenantSecrets>();
            })
            .Build();

        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        int code;
        try
        {
            Console.SetOut(writer);
            code = await CliCommandTree.Build(host).InvokeAsync(["migration-status", "--require-current"]);
        }
        finally
        {
            Console.SetOut(originalOut);
            SqlConnection.ClearPool(new SqlConnection(connectionString));
        }

        var output = writer.ToString();
        Assert.Equal(0, code);
        Assert.Contains("Not assigned to Wasla Orders, not checked: gate-moving, gate-pos", output, StringComparison.Ordinal);
        Assert.Contains("All active databases are current for this build.", output, StringComparison.Ordinal);
    }

    private sealed class NoTenantSecrets : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            throw new InvalidOperationException("No secret may be encrypted by these tests.");

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            throw new InvalidOperationException("No tenant connection string may be decrypted by these tests.");
    }
}
