using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Persistence;

/// <summary>
/// The tenant-database entities live in <c>Wasla.Domain.Entities.TenantDb</c> (WAS-142). The model, its snapshot and
/// the newest migration name them there, and every entity keeps the table it was mapped to before the move, so the
/// namespace carries no physical schema change. Built on the SQL Server provider; no database is opened.
/// </summary>
public sealed class TenantDbEntityNamespaceTests
{
    private const string TenantDbNamespace = "Wasla.Domain.Entities.TenantDb";

    /// <summary>Every tenant-database entity and its table, as created by the tenant migrations.</summary>
    private static readonly IReadOnlyDictionary<Type, string> Tables = new Dictionary<Type, string>
    {
        [typeof(AppUser)] = "AppUsers",
        [typeof(Branch)] = "Branches",
        [typeof(GuidedDemoSession)] = "GuidedDemoSessions",
        [typeof(IntegrationError)] = "IntegrationErrors",
        [typeof(Order)] = "Orders",
        [typeof(OrderItem)] = "OrderItems",
        [typeof(OrderItemOption)] = "OrderItemOptions",
        [typeof(PasswordResetToken)] = "PasswordResetTokens",
        [typeof(PlatformConnection)] = "PlatformConnections",
        [typeof(PrintJob)] = "PrintJobs",
        [typeof(ProviderWebhookEvent)] = "ProviderWebhookEvents",
        [typeof(RestaurantLogo)] = "RestaurantLogos",
        [typeof(SyncLog)] = "SyncLogs",
        [typeof(TenantOperationalSettings)] = "TenantOperationalSettings",
        [typeof(TenantSession)] = "TenantSessions",
        [typeof(UserGuidedSetupState)] = "UserGuidedSetupStates",
        [typeof(UserNotificationSettings)] = "UserNotificationSettings",
        [typeof(UserOrderActionPreference)] = "UserOrderActionPreferences",
        [typeof(UserProductTourCompletion)] = "UserProductTourCompletions",
    };

    private static TenantDbContext SqlServerModel() => new(new DbContextOptionsBuilder<TenantDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void EveryTenantEntity_IsATenantDbType_MappedToItsTable_AndEveryTenantDbTypeIsAnEntity()
    {
        using var tenant = SqlServerModel();
        var entities = tenant.Model.GetEntityTypes().ToList();

        Assert.Equal(Names(Tables.Keys), Names(entities.Select(e => e.ClrType)));
        Assert.All(entities, entity =>
        {
            Assert.Equal(TenantDbNamespace, entity.ClrType.Namespace);
            Assert.Equal($"{TenantDbNamespace}.{entity.ClrType.Name}", entity.Name);
            Assert.Equal(Tables[entity.ClrType], entity.GetTableName());
            Assert.Null(entity.GetSchema());
        });

        // The namespace holds the tenant-database entities and nothing else.
        var declared = typeof(Order).Assembly.GetTypes().Where(type => type.Namespace == TenantDbNamespace && !type.IsNested);
        Assert.Equal(Names(Tables.Keys), Names(declared));
    }

    [Fact]
    public void TheSnapshot_AndTheNewestMigration_NameTheSameTenantDbEntities_AndMatchTheModel()
    {
        using var tenant = SqlServerModel();
        var runtime = tenant.GetService<IDesignTimeModel>().Model;
        var migrations = tenant.GetService<IMigrationsAssembly>();
        var expected = runtime.GetEntityTypes().Select(e => e.Name).Order(StringComparer.Ordinal).ToList();

        var snapshot = Finalize(tenant, migrations.ModelSnapshot!.Model);
        var newest = migrations.Migrations.MaxBy(m => m.Key, StringComparer.Ordinal);
        var target = Finalize(tenant, migrations.CreateMigration(newest.Value, tenant.Database.ProviderName!).TargetModel!);

        Assert.Equal(expected, snapshot.GetEntityTypes().Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expected, target.GetEntityTypes().Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.All(expected, name => Assert.StartsWith($"{TenantDbNamespace}.", name, StringComparison.Ordinal));

        // Same tables, columns, keys, indexes and constraints: the comparison `migrations add` makes.
        var differ = tenant.GetService<IMigrationsModelDiffer>();
        Assert.False(differ.HasDifferences(snapshot.GetRelationalModel(), runtime.GetRelationalModel()));
        Assert.False(differ.HasDifferences(target.GetRelationalModel(), runtime.GetRelationalModel()));
    }

    [Fact]
    public void EveryMigrationAfterTheHistoricalOnes_NamesOnlyTenantDbEntities()
    {
        using var tenant = SqlServerModel();
        var migrations = tenant.GetService<IMigrationsAssembly>();

        // Migrations with a designer, oldest first; for each, whether its entities are named in the TenantDb namespace.
        var designers = migrations.Migrations
            .OrderBy(migration => migration.Key, StringComparer.Ordinal)
            .Select(migration => (Id: migration.Key, Model: migrations.CreateMigration(migration.Value, tenant.Database.ProviderName!).TargetModel))
            .Where(migration => migration.Model?.GetEntityTypes().Any() == true)
            .Select(migration => (migration.Id, Canonical: migration.Model!.GetEntityTypes()
                .Select(entity => entity.Name.StartsWith($"{TenantDbNamespace}.", StringComparison.Ordinal))
                .Distinct()
                .ToList()))
            .ToList();

        // A designer names all of its entities one way, never a mix.
        Assert.All(designers, designer => Assert.True(designer.Canonical.Count == 1, designer.Id));

        // The historical designers kept by WAS-134 (at most 15, a list that may only shrink) all come first, and every
        // designer after them, including any new migration, names only TenantDb entities.
        var historical = designers.TakeWhile(designer => !designer.Canonical[0]).Count();
        Assert.InRange(historical, 0, 15);
        Assert.All(designers.Skip(historical), designer => Assert.True(designer.Canonical[0], designer.Id));
        Assert.True(designers[^1].Canonical[0], designers[^1].Id);
    }

    [Fact]
    public void TheModel_CreatesExactlyTheTenantDatabaseTables()
    {
        using var tenant = SqlServerModel();

        var tables = tenant.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Select(table => table.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(Tables.Values.Order(StringComparer.Ordinal), tables);
    }

    private static IModel Finalize(DbContext context, IModel model)
    {
        if (model is IMutableModel mutable)
            model = mutable.FinalizeModel();
        return context.GetService<IModelRuntimeInitializer>().Initialize(model);
    }

    private static List<string> Names(IEnumerable<Type> types) =>
        types.Select(type => type.FullName!).Order(StringComparer.Ordinal).ToList();
}
