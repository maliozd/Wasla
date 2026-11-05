using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Security;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Orders;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// The <c>AddTenantSessions</c> tenant migration on a real SQL Server (a throwaway database migrated with this build's
/// migrations, see <see cref="SqlServerTenantDatabase"/>): the table, keys and index it creates, and that it reverts and
/// applies again. The SQLite web-host tests build their schema with <c>EnsureCreated</c>, which proves neither.
/// </summary>
public sealed class TenantSessionMigrationSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string PreviousMigration = "20261026202445_AddProviderWebhookEvents";
    private const string SessionsMigration = "20261103201038_AddTenantSessions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_CreatesTheSessionTable_AndReverts()
    {
        await using var db = database.Open();
        Assert.Contains(SessionsMigration, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        Assert.Equal(
            [
                ("CreatedAtUtc", "datetime2", false),
                ("Id", "uniqueidentifier", false),
                ("RevokedAtUtc", "datetime2", true),
                ("UserId", "uniqueidentifier", false)
            ],
            await ColumnsAsync(db));
        Assert.Equal(["Id"], await StringsAsync(db, """
            SELECT c.name FROM sys.key_constraints k
            JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE k.type = 'PK' AND k.name = 'PK_TenantSessions' AND k.parent_object_id = OBJECT_ID('dbo.TenantSessions')
            """));
        Assert.Equal(["FK_TenantSessions_AppUsers_UserId|AppUsers|CASCADE"], await StringsAsync(db, """
            SELECT fk.name, OBJECT_NAME(fk.referenced_object_id), fk.delete_referential_action_desc
            FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.TenantSessions')
            """));
        Assert.Equal(["IX_TenantSessions_UserId|UserId"], await StringsAsync(db, """
            SELECT i.name, c.name FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.TenantSessions') AND i.is_primary_key = 0
            """));

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Ct);
        Assert.Empty(await StringsAsync(db, "SELECT name FROM sys.tables WHERE name = 'TenantSessions'"));
        Assert.Equal(["AppUsers"], await StringsAsync(db, "SELECT name FROM sys.tables WHERE name = 'AppUsers'"));

        await migrator.MigrateAsync(SessionsMigration, Ct);
        Assert.Equal(["TenantSessions"], await StringsAsync(db, "SELECT name FROM sys.tables WHERE name = 'TenantSessions'"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    private static async Task<List<(string Name, string Type, bool Nullable)>> ColumnsAsync(TenantDbContext db)
    {
        var rows = new List<(string, string, bool)>();
        await using var command = await CommandAsync(db, """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'TenantSessions' ORDER BY COLUMN_NAME
            """);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2) == "YES"));
        return rows;
    }

    /// <summary>One string per row: its columns joined with <c>|</c> (joined here, as catalog columns differ in collation).</summary>
    private static async Task<List<string>> StringsAsync(TenantDbContext db, string sql)
    {
        var rows = new List<string>();
        await using var command = await CommandAsync(db, sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)));
        return rows;
    }

    private static async Task<System.Data.Common.DbCommand> CommandAsync(TenantDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(Ct);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
}

/// <summary>
/// The session registry and validator on a real SQL Server (WAS-114): every caller uses its own service, DbContext and
/// connection pool (<see cref="SqlServerTenantDatabase.Instance"/>), as separate Web instances would. Covers creation,
/// validation, idempotent revocation scoped to one session and one user, the cascade on user deletion, simultaneous
/// logouts of one session, and a logout that waits on another instance's uncommitted revocation.
/// </summary>
public sealed class TenantSessionRegistrySqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    // The databases of SqlServerTenantDatabase are selected by the fixture, not by this id; it only has to match the claims.
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sessions_AreCreatedValidatedAndRevokedOneAtATime_ForTheirOwnUserOnly()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();
        var registry = Registry("Web-A");
        var validator = new TenantSessionValidator(database.Instance("Web-B"), NullLogger<TenantSessionValidator>.Instance);

        var first = await registry.CreateAsync(TenantId, user.Id, Ct);
        var second = await registry.CreateAsync(TenantId, user.Id, Ct);
        var othersSession = await registry.CreateAsync(TenantId, other.Id, Ct);
        Assert.Equal(3, new[] { first, second, othersSession }.Distinct().Count());
        Assert.Equal(TenantSessionState.Valid, await validator.ValidateAsync(TenantId, Principal(user, first), Ct));
        Assert.Equal(TenantSessionState.Valid, await validator.ValidateAsync(TenantId, Principal(user, second), Ct));

        Assert.True(await registry.RevokeAsync(TenantId, user.Id, first, Ct));
        var revokedAt = (await ReadAsync(first))!.RevokedAtUtc;
        Assert.NotNull(revokedAt);
        Assert.Equal(TenantSessionState.SessionRevoked, await validator.ValidateAsync(TenantId, Principal(user, first), Ct));
        Assert.Equal(TenantSessionState.Valid, await validator.ValidateAsync(TenantId, Principal(user, second), Ct));

        // Repeated, for another user's session, or for an unknown id: nothing is revoked, created or reactivated.
        Assert.False(await registry.RevokeAsync(TenantId, user.Id, first, Ct));
        Assert.False(await registry.RevokeAsync(TenantId, user.Id, othersSession, Ct));
        Assert.False(await registry.RevokeAsync(TenantId, user.Id, Guid.NewGuid(), Ct));
        Assert.Equal(revokedAt, (await ReadAsync(first))!.RevokedAtUtc);
        Assert.Null((await ReadAsync(othersSession))!.RevokedAtUtc);
        Assert.Equal(TenantSessionState.SessionNotFound, await validator.ValidateAsync(TenantId, Principal(user, othersSession), Ct));
        Assert.Equal(TenantSessionState.SessionNotFound, await validator.ValidateAsync(TenantId, Principal(user, Guid.NewGuid()), Ct));
        Assert.Equal(TenantSessionState.Valid, await validator.ValidateAsync(TenantId, Principal(other, othersSession), Ct));
        Assert.Equal(3, await CountAsync(user.Id, other.Id));

        // Deleting a user removes that user's sessions with it.
        await using (var db = database.Open())
            await db.AppUsers.Where(u => u.Id == user.Id).ExecuteDeleteAsync(Ct);
        Assert.Equal(1, await CountAsync(user.Id, other.Id));
    }

    [Fact]
    public async Task SimultaneousSignInsAndLogouts_FromSeparateInstances_RevokeOnlyOnce()
    {
        var user = await SeedUserAsync();
        const int callers = 8;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Simultaneous sign-ins: each gets its own stored session.
        var signIns = Enumerable.Range(0, callers)
            .Select(i => Registry($"SignIn-{i}"))
            .Select(registry => Task.Run(async () =>
            {
                await go.Task;
                return await registry.CreateAsync(TenantId, user.Id, Ct);
            }, Ct))
            .ToList();
        go.SetResult();
        var created = await Task.WhenAll(signIns);
        Assert.Equal(callers, created.Distinct().Count());
        Assert.Equal(callers, await CountAsync(user.Id));

        // Simultaneous logouts of one session: one revokes it, with its own time; every other is a no-op.
        var target = created[0];
        var times = Enumerable.Range(0, callers).Select(i => new DateTime(2026, 11, 3, 10, i, 0, DateTimeKind.Utc)).ToArray();
        var logoutGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logouts = Enumerable.Range(0, callers)
            .Select(i => (Index: i, Registry: Registry($"Logout-{i}", times[i])))
            .Select(caller => Task.Run(async () =>
            {
                await logoutGo.Task;
                return (caller.Index, Revoked: await caller.Registry.RevokeAsync(TenantId, user.Id, target, Ct));
            }, Ct))
            .ToList();
        logoutGo.SetResult();
        var results = await Task.WhenAll(logouts);

        var winner = Assert.Single(results, r => r.Revoked);
        Assert.Equal(times[winner.Index], (await ReadAsync(target))!.RevokedAtUtc);
        foreach (var session in created.Skip(1))
            Assert.Null((await ReadAsync(session))!.RevokedAtUtc);
    }

    [Fact]
    public async Task ALogoutWaitingOnAnotherInstancesUncommittedRevocation_DoesNotOverwriteIt()
    {
        var user = await SeedUserAsync();
        var session = await Registry("Web-A").CreateAsync(TenantId, user.Id, Ct);
        var firstTime = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

        // Another instance's revocation, written and not yet committed: it holds the session row.
        await using var holder = database.Open("Holder");
        await using var transaction = await holder.Database.BeginTransactionAsync(Ct);
        Assert.Equal(1, await holder.TenantSessions
            .Where(s => s.Id == session && s.UserId == user.Id && s.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, firstTime), Ct));
        var holderSession = await holder.Database.SqlQuery<short>($"SELECT @@SPID AS [Value]").SingleAsync(Ct);

        var second = Registry("Web-B", firstTime.AddMinutes(5)).RevokeAsync(TenantId, user.Id, session, Ct);
        await WaitUntilBlockedAsync(holderSession, second);
        await transaction.CommitAsync(Ct);

        Assert.False(await second.WaitAsync(Deadline, Ct));
        Assert.Equal(firstTime, (await ReadAsync(session))!.RevokedAtUtc);
        var validator = new TenantSessionValidator(database.Instance("Web-C"), NullLogger<TenantSessionValidator>.Instance);
        Assert.Equal(TenantSessionState.SessionRevoked, await validator.ValidateAsync(TenantId, Principal(user, session), Ct));
    }

    private TenantSessionRegistry Registry(string instance, DateTime? now = null) => new(
        database.Instance(instance),
        now is { } fixedTime ? new FixedClock(fixedTime) : TimeProvider.System,
        NullLogger<TenantSessionRegistry>.Instance);

    private static ClaimsPrincipal Principal(AppUser user, Guid sessionId) =>
        new(new ClaimsIdentity(TenantSessionClaims.Create(TenantId, user.Id, user.Role, user.SecurityStamp, sessionId), AuthSchemes.Tenant));

    private async Task<AppUser> SeedUserAsync()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@sessions.test",
            PasswordHash = "not-a-real-hash",
            FullName = "Session Test",
            Role = UserRole.Owner,
            IsActive = true
        };
        await using var db = database.Open();
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<Wasla.Domain.Entities.TenantDb.TenantSession?> ReadAsync(Guid sessionId)
    {
        await using var db = database.Open();
        return await db.TenantSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, Ct);
    }

    private async Task<int> CountAsync(params Guid[] userIds)
    {
        await using var db = database.Open();
        return await db.TenantSessions.CountAsync(s => userIds.Contains(s.UserId), Ct);
    }

    private async Task WaitUntilBlockedAsync(short blockingSession, Task waiter)
    {
        await using var inspector = database.Open("Inspector");
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            Assert.False(waiter.IsCompleted, "The second logout finished instead of waiting for the session row.");
            var blocked = await inspector.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {blockingSession}")
                .SingleAsync(Ct);
            if (blocked > 0)
                return;
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The second logout was never blocked by the instance holding the session row.");
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
