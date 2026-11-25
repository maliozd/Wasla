using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-241 migration on a real SQL Server (a throwaway database, see <see cref="SqlServerTenantDatabase"/>): it only adds
/// two nullable columns with the connection's lengths, leaves every order stored before it without a store (nothing is
/// inferred or backfilled), and can be reverted and applied again.
/// </summary>
public sealed class OrderStoreIdentityMigrationSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string PreviousMigration = "20261109191122_AddUserOrderActionPreferences";
    private const string StoreIdentityMigration = "20261125185644_AddOrderStoreIdentity";
    private static readonly DateTime Imported = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdersStoredBeforeTheMigration_HaveNoStore_AndItReverts()
    {
        await using var db = database.Open();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration, Ct);
        // Open and finished orders, with a payload that names a store: the migration must not read it.
        var openBefore = await InsertLegacyOrderAsync(db, "legacy-new", OrderStatus.New);
        var preparingBefore = await InsertLegacyOrderAsync(db, "legacy-preparing", OrderStatus.Preparing);
        var deliveredBefore = await InsertLegacyOrderAsync(db, "legacy-delivered", OrderStatus.Delivered);

        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Contains(StoreIdentityMigration, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(["StoreId:nvarchar:256:1", "SupplierId:nvarchar:128:1"], await StoreColumnsAsync(db));

        db.ChangeTracker.Clear();
        foreach (var id in new[] { openBefore, preparingBefore, deliveredBefore })
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
            Assert.Null(order.StoreId);
            Assert.Null(order.SupplierId);
        }

        // The longest values a connection can hold fit.
        Assert.Equal(1, await db.Orders.Where(o => o.Id == openBefore).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.StoreId, new string('s', 128))
            .SetProperty(o => o.SupplierId, new string('p', 64)), Ct));

        await migrator.MigrateAsync(PreviousMigration, Ct);
        Assert.Empty(await StoreColumnsAsync(db));
        Assert.Equal(3, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [Orders]").SingleAsync(Ct));

        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Equal(["StoreId:nvarchar:256:1", "SupplierId:nvarchar:128:1"], await StoreColumnsAsync(db));
        db.ChangeTracker.Clear();
        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == openBefore, Ct)).StoreId);
    }

    /// <summary>Name, type, byte length and nullability of the store columns on Orders.</summary>
    private static async Task<List<string>> StoreColumnsAsync(DbContext db)
    {
        var columns = await db.Database
            .SqlQuery<string>($"""
                SELECT c.[name] + ':' + t.[name] + ':' + CAST(c.[max_length] AS varchar(10)) + ':' + CAST(c.[is_nullable] AS varchar(1)) AS [Value]
                FROM sys.columns c JOIN sys.types t ON t.[user_type_id] = c.[user_type_id]
                WHERE c.[object_id] = OBJECT_ID(N'Orders') AND c.[name] IN (N'StoreId', N'SupplierId')
                """)
            .ToListAsync(Ct);
        return columns.Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<Guid> InsertLegacyOrderAsync(DbContext db, string externalId, OrderStatus status)
    {
        var id = Guid.NewGuid();
        var payload = $$"""{"id":"{{externalId}}","storeId":"store-a","supplierId":"supplier-a"}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Orders] ([Id], [Platform], [ExternalOrderId], [ExternalOrderCode], [IdempotencyKey], [InternalStatus],
                [PlatformStatus], [CustomerName], [CustomerPhone], [CustomerAddress], [TotalAmount], [DeliveryFee], [ServiceFee],
                [PaymentMethod], [PaymentStatus], [ReceivedAt], [RawPayloadJson], [CreatedAt], [UpdatedAt], [ChangeVersion])
            VALUES ({id}, {(int)FoodPlatform.TrendyolYemek}, {externalId}, {externalId}, {externalId}, {(int)status},
                'Created', 'Test', '', '', 100, 0, 0, 0, 0, {Imported}, {payload}, {Imported}, {Imported}, 1);
            """, Ct);
        return id;
    }
}
