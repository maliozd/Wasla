using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-125 migration on a real SQL Server (a throwaway database, see <see cref="SqlServerTenantDatabase"/>): an order
/// stored before it keeps its stored provider time as the order time, the stored <c>OrderedAt</c> column follows
/// later writes, and the migration can be reverted. SQLite cannot prove the computed column's DDL or the
/// nullable change on SQL Server.
/// </summary>
public sealed class OrderTimeMigrationSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string PreviousMigration = "20261014155220_AddOrderItemSequence";
    private const string OrderTimeMigration = "20261017060726_AddOrderTimeSemantics";

    private static readonly DateTime Placed = new(2026, 10, 16, 17, 40, 0, DateTimeKind.Utc);
    private static readonly DateTime Imported = new(2026, 10, 16, 23, 19, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LegacyOrders_KeepTheirStoredProviderTime_AndTheMigrationReverts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = database.Open();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration, ct);
        var legacyId = Guid.NewGuid();
        const string payload = "{}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Orders] ([Id], [Platform], [ExternalOrderId], [ExternalOrderCode], [IdempotencyKey], [InternalStatus],
                [PlatformStatus], [CustomerName], [CustomerPhone], [CustomerAddress], [TotalAmount], [DeliveryFee], [ServiceFee],
                [PaymentMethod], [PaymentStatus], [CreatedAtPlatform], [ReceivedAt], [RawPayloadJson], [CreatedAt], [UpdatedAt], [ChangeVersion])
            VALUES ({legacyId}, {(int)FoodPlatform.TrendyolYemek}, 'legacy-125', 'TY-LEGACY-125', 'legacy-125-key', {(int)OrderStatus.Delivered},
                'Delivered', 'Test', '', '', 100, 0, 0, 0, 0, {Placed}, {Imported}, {payload}, {Imported}, {Imported}, 1);
            """, ct);

        // Trendyol GO stored a missing packageCreationDate as 1970-01-01 before WAS-125.
        var epochId = Guid.NewGuid();
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Orders] ([Id], [Platform], [ExternalOrderId], [ExternalOrderCode], [IdempotencyKey], [InternalStatus],
                [PlatformStatus], [CustomerName], [CustomerPhone], [CustomerAddress], [TotalAmount], [DeliveryFee], [ServiceFee],
                [PaymentMethod], [PaymentStatus], [CreatedAtPlatform], [ReceivedAt], [RawPayloadJson], [CreatedAt], [UpdatedAt], [ChangeVersion])
            VALUES ({epochId}, {(int)FoodPlatform.TrendyolYemek}, 'epoch-125', 'TY-EPOCH-125', 'epoch-125-key', {(int)OrderStatus.Delivered},
                'Delivered', 'Test', '', '', 100, 0, 0, 0, 0, {epoch}, {Imported}, {payload}, {Imported}, {Imported}, 1);
            """, ct);

        await migrator.MigrateAsync(OrderTimeMigration, ct);

        db.ChangeTracker.Clear();
        var epochRow = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == epochId, ct);
        Assert.Null(epochRow.CreatedAtPlatform);
        Assert.Equal(Imported, epochRow.OrderedAt);

        var legacy = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == legacyId, ct);
        Assert.Equal(Placed, legacy.CreatedAtPlatform);
        Assert.Equal(Placed, legacy.OrderedAt);
        Assert.Equal(Imported, legacy.ReceivedAt);
        Assert.Null(legacy.PlatformModifiedAt);

        // A new order without a provider time: the stored column falls back to the import time, and follows a later
        // provider time written by EF (the computed value comes back from the same save).
        var fallback = new Order
        {
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = "fallback-125",
            ExternalOrderCode = "TY-FALLBACK-125",
            IdempotencyKey = "fallback-125-key",
            PlatformStatus = "Created",
            ReceivedAt = Imported,
            RawPayloadJson = "{}"
        };
        db.Orders.Add(fallback);
        await db.SaveChangesAsync(ct);
        Assert.Null(fallback.CreatedAtPlatform);
        Assert.Equal(Imported, fallback.OrderedAt);

        fallback.CreatedAtPlatform = Placed;
        await db.SaveChangesAsync(ct);
        Assert.Equal(Placed, fallback.OrderedAt);
        fallback.CreatedAtPlatform = null;
        await db.SaveChangesAsync(ct);
        Assert.Equal(Imported, fallback.OrderedAt);

        var indexes = await db.Database
            .SqlQuery<string>($"SELECT [name] AS [Value] FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'Orders') AND [name] = N'IX_Orders_OrderedAt'")
            .ToListAsync(ct);
        Assert.Single(indexes);

        // Down: the column becomes required again, and an order without a provider time gets its import time back.
        await migrator.MigrateAsync(PreviousMigration, ct);
        var restored = await db.Database
            .SqlQuery<DateTime>($"SELECT [CreatedAtPlatform] AS [Value] FROM [Orders] WHERE [Id] = {fallback.Id}")
            .SingleAsync(ct);
        Assert.Equal(Imported, restored);

        await migrator.MigrateAsync(cancellationToken: ct);
    }
}
