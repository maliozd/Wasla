using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The <c>AddUserOrderActionPreferences</c> tenant migration on a real SQL Server (a throwaway database): one row per
/// employee, removed with the employee, and a migration that reverts and applies again.
/// </summary>
public sealed class UserOrderActionPreferenceMigrationSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string PreviousMigration = "20261103201038_AddTenantSessions";
    private const string Migration = "20261109113343_AddUserOrderActionPreferences";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_CreatesTheTable_AndReverts()
    {
        await using var db = database.Open();
        Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        Assert.Equal(["IX_UserOrderActionPreferences_UserId|1"], await StringsAsync(db, """
            SELECT i.name, CAST(i.is_unique AS int) FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID('dbo.UserOrderActionPreferences') AND i.is_primary_key = 0
            """));
        Assert.Equal(["FK_UserOrderActionPreferences_AppUsers_UserId|AppUsers|CASCADE"], await StringsAsync(db, """
            SELECT fk.name, OBJECT_NAME(fk.referenced_object_id), fk.delete_referential_action_desc
            FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.UserOrderActionPreferences')
            """));

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Ct);
        Assert.Empty(await StringsAsync(db, "SELECT name FROM sys.tables WHERE name = 'UserOrderActionPreferences'"));
        Assert.Equal(["AppUsers"], await StringsAsync(db, "SELECT name FROM sys.tables WHERE name = 'AppUsers'"));

        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(["UserOrderActionPreferences"], await StringsAsync(db, "SELECT name FROM sys.tables WHERE name = 'UserOrderActionPreferences'"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    private static async Task<List<string>> StringsAsync(TenantDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values));
        }

        return rows;
    }
}
