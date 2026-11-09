using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-200 migration on a real SQL Server (a throwaway database, see <see cref="SqlServerTenantDatabase"/>): the columns
/// are nullable, orders that ended before it stay unknown with nothing inferred, the check constraint refuses metadata
/// that does not fit its order, and the migration can be reverted.
/// </summary>
public sealed class OrderCancellationMigrationSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string PreviousMigration = "20261103201038_AddTenantSessions";
    private const string CancellationMigration = "20261109051847_AddOrderCancellationOrigin";
    private const int CheckConstraintViolation = 547;

    private static readonly string[] MetadataColumns = ["CancellationOrigin", "CancellationReasonCode", "CancellationReasonPlatform"];
    private static readonly DateTime Imported = new(2026, 10, 16, 23, 19, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdersEndedBeforeTheMigration_StayUnknown_TheConstraintHolds_AndItReverts()
    {
        await using var db = database.Open();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration, Ct);
        // A rejected order (UnSupplied) and a cancelled one: their provider status could suggest an origin. It must not.
        var rejectedBefore = await InsertLegacyOrderAsync(db, "legacy-unsupplied", OrderStatus.Cancelled, "UnSupplied");
        var cancelledBefore = await InsertLegacyOrderAsync(db, "legacy-cancelled", OrderStatus.Cancelled, "Cancelled");
        var deliveredBefore = await InsertLegacyOrderAsync(db, "legacy-delivered", OrderStatus.Delivered, "Delivered");

        // Through the latest migration: the reads below use the current order model.
        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Contains(CancellationMigration, await db.Database.GetAppliedMigrationsAsync(Ct));

        var columns = await db.Database
            .SqlQuery<string>($"""
                SELECT c.[name] + ':' + t.[name] + ':' + CAST(c.[is_nullable] AS varchar(1)) AS [Value]
                FROM sys.columns c JOIN sys.types t ON t.[user_type_id] = c.[user_type_id]
                WHERE c.[object_id] = OBJECT_ID(N'Orders') AND c.[name] LIKE N'Cancellation%' AND c.[name] <> N'CancelledAt'
                """)
            .ToListAsync(Ct);
        Assert.Equal(MetadataColumns.Select(c => $"{c}:int:1").Order(), columns.Order());

        db.ChangeTracker.Clear();
        foreach (var id in new[] { rejectedBefore, cancelledBefore, deliveredBefore })
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
            Assert.Null(order.CancellationOrigin);
            Assert.Null(order.CancellationReasonPlatform);
            Assert.Null(order.CancellationReasonCode);
        }

        await AssertRefusedAsync(db, cancelledBefore, "[CancellationReasonCode] = 621");
        await AssertRefusedAsync(db, cancelledBefore, $"[CancellationReasonPlatform] = {(int)FoodPlatform.TrendyolYemek}");
        await AssertRefusedAsync(db, cancelledBefore, $"[CancellationReasonCode] = 621, [CancellationReasonPlatform] = {(int)FoodPlatform.TrendyolYemek}");
        await AssertRefusedAsync(db, cancelledBefore,
            $"[CancellationOrigin] = 1, [CancellationReasonCode] = 621, [CancellationReasonPlatform] = {(int)FoodPlatform.Yemeksepeti}");
        await AssertRefusedAsync(db, cancelledBefore, "[CancellationOrigin] = 1, [CancellationReasonCode] = 621");
        await AssertRefusedAsync(db, deliveredBefore, "[CancellationOrigin] = 4");

        Assert.Equal(1, await UpdateAsync(db, cancelledBefore,
            $"[CancellationOrigin] = 1, [CancellationReasonCode] = 621, [CancellationReasonPlatform] = {(int)FoodPlatform.TrendyolYemek}"));
        Assert.Equal(1, await UpdateAsync(db, rejectedBefore, "[CancellationOrigin] = 3"));

        await migrator.MigrateAsync(PreviousMigration, Ct);
        var remaining = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'Orders') AND [name] IN (N'CancellationOrigin', N'CancellationReasonCode', N'CancellationReasonPlatform')")
            .SingleAsync(Ct);
        Assert.Equal(0, remaining);

        await migrator.MigrateAsync(cancellationToken: Ct);
    }

    private static Task<int> UpdateAsync(DbContext db, Guid orderId, string assignments) =>
#pragma warning disable EF1002 // Constant test SQL; only the id is a value.
        db.Database.ExecuteSqlRawAsync($"UPDATE [Orders] SET {assignments} WHERE [Id] = {{0}}", [orderId], Ct);
#pragma warning restore EF1002

    private static async Task AssertRefusedAsync(DbContext db, Guid orderId, string assignments)
    {
        var refused = await Assert.ThrowsAsync<SqlException>(() => UpdateAsync(db, orderId, assignments));
        Assert.Equal(CheckConstraintViolation, refused.Number);
    }

    private static async Task<Guid> InsertLegacyOrderAsync(DbContext db, string externalId, OrderStatus status, string platformStatus)
    {
        var id = Guid.NewGuid();
        const string payload = "{}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Orders] ([Id], [Platform], [ExternalOrderId], [ExternalOrderCode], [IdempotencyKey], [InternalStatus],
                [PlatformStatus], [CustomerName], [CustomerPhone], [CustomerAddress], [TotalAmount], [DeliveryFee], [ServiceFee],
                [PaymentMethod], [PaymentStatus], [ReceivedAt], [RawPayloadJson], [CreatedAt], [UpdatedAt], [ChangeVersion])
            VALUES ({id}, {(int)FoodPlatform.TrendyolYemek}, {externalId}, {externalId}, {externalId}, {(int)status},
                {platformStatus}, 'Test', '', '', 100, 0, 0, 0, 0, {Imported}, {payload}, {Imported}, {Imported}, 1);
            """, Ct);
        return id;
    }
}
