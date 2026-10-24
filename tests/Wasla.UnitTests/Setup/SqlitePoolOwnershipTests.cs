using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.UnitTests.Admin;
using Wasla.UnitTests.GuidedSetup;

namespace Wasla.UnitTests.Setup;

/// <summary>
/// Test classes run in parallel, and each SQLite test helper owns its own temporary databases. Cleaning up one helper
/// must release that helper's files and leave every other helper's connections alone. Real Microsoft.Data.Sqlite
/// connections and pools; the other helper's cleanup runs on another thread while this one is in the middle of its work.
/// </summary>
public sealed class SqlitePoolOwnershipTests
{
    public enum Helper
    {
        OperationalModeTestDatabases,
        GuidedSetupTenantDatabases,
        CentralTestDatabase,
        RecordingTenantDbFactory
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Helper.OperationalModeTestDatabases)]
    [InlineData(Helper.GuidedSetupTenantDatabases)]
    [InlineData(Helper.CentralTestDatabase)]
    [InlineData(Helper.RecordingTenantDbFactory)]
    public async Task AnotherHelpersCleanup_WhileThisConnectionOpens_LeavesTheConnectionUsable(Helper other)
    {
        using var databases = new OperationalModeTestDatabases();
        var tenant = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var used = await UseAsync(other);
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var work = Task.Run(async () =>
        {
            await using var db = await databases.CreateAsync(tenant, Ct);
            await db.Database.OpenConnectionAsync(Ct);
            var finishOpen = HoldInsideOpen((SqliteConnection)db.Database.GetDbConnection());
            try
            {
                opening.SetResult();
                await cleanedUp.Task;
            }
            finally
            {
                finishOpen();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.AppUsers.Add(User(userId));
            await db.SaveChangesAsync(Ct);
            Assert.Equal(1, await db.AppUsers.CountAsync(user => user.Id == userId, Ct));
            await transaction.CommitAsync(Ct);
        }, Ct);

        await Task.WhenAny(opening.Task, work);
        if (work.IsCompleted)
            await work;
        try
        {
            await Task.Run(used.Owner.Dispose, Ct);
        }
        finally
        {
            cleanedUp.TrySetResult();
        }

        await work;
        Assert.False(Path.Exists(used.Path), $"{other} left {used.Path} behind.");
        await using var check = await databases.CreateAsync(tenant, Ct);
        Assert.Equal(1, await check.AppUsers.CountAsync(user => user.Id == userId, Ct));
    }

    [Theory]
    [InlineData(Helper.OperationalModeTestDatabases)]
    [InlineData(Helper.GuidedSetupTenantDatabases)]
    [InlineData(Helper.CentralTestDatabase)]
    [InlineData(Helper.RecordingTenantDbFactory)]
    public async Task AnotherHelpersCleanup_LeavesThisTransactionAndThisPoolAlone(Helper other)
    {
        using var databases = new OperationalModeTestDatabases();
        var tenant = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var used = await UseAsync(other);

        // One connection idle in this database's pool, one open in a transaction with an uncommitted row.
        await using var idle = await databases.CreateAsync(tenant, Ct);
        await idle.Database.OpenConnectionAsync(Ct);
        var idleHandle = Handle(idle);
        await using var busy = await databases.CreateAsync(tenant, Ct);
        await busy.Database.OpenConnectionAsync(Ct);
        var busyHandle = Handle(busy);
        await idle.Database.CloseConnectionAsync();
        await using var transaction = await busy.Database.BeginTransactionAsync(Ct);
        busy.AppUsers.Add(User(first));
        await busy.SaveChangesAsync(Ct);

        await Task.Run(used.Owner.Dispose, Ct);

        busy.AppUsers.Add(User(second));
        await busy.SaveChangesAsync(Ct);
        Assert.Equal(2, await busy.AppUsers.CountAsync(Ct));
        await transaction.CommitAsync(Ct);
        await busy.Database.CloseConnectionAsync();

        // Both connections went back to this database's pool: the next two opens reuse them.
        await using var again = await databases.CreateAsync(tenant, Ct);
        await again.Database.OpenConnectionAsync(Ct);
        await using var alsoAgain = await databases.CreateAsync(tenant, Ct);
        await alsoAgain.Database.OpenConnectionAsync(Ct);
        Assert.True(
            ReferenceEquals(Handle(again), busyHandle) && ReferenceEquals(Handle(alsoAgain), idleHandle),
            $"{other}'s cleanup cleared this database's connection pool.");
        Assert.Equal(2, await alsoAgain.AppUsers.CountAsync(Ct));
        Assert.False(Path.Exists(used.Path), $"{other} left {used.Path} behind.");
    }

    [Theory]
    [InlineData(Helper.OperationalModeTestDatabases)]
    [InlineData(Helper.GuidedSetupTenantDatabases)]
    [InlineData(Helper.CentralTestDatabase)]
    [InlineData(Helper.RecordingTenantDbFactory)]
    public async Task OwnCleanup_ReleasesAndDeletesTheHelpersFiles(Helper helper)
    {
        var used = await UseAsync(helper);
        if (used.Pooled && OperatingSystem.IsWindows())
            Assert.True(IsHeldOpen(used.DatabaseFile), "The helper's pool should still hold its database file before cleanup.");

        used.Owner.Dispose();

        Assert.False(Path.Exists(used.Path), $"{helper} left {used.Path} behind.");
    }

    /// <summary>A helper whose databases were created, written and read, every context disposed again.</summary>
    private sealed record UsedHelper(IDisposable Owner, string Path, string DatabaseFile, bool Pooled);

    private static async Task<UsedHelper> UseAsync(Helper helper)
    {
        var tenant = Guid.NewGuid();
        switch (helper)
        {
            case Helper.OperationalModeTestDatabases:
            {
                var databases = new OperationalModeTestDatabases();
                await databases.SeedUserAsync(tenant, Guid.NewGuid());
                await using var db = await databases.CreateAsync(tenant, Ct);
                Assert.Equal(1, await db.AppUsers.CountAsync(Ct));
                var file = DataSource(db.Database.GetConnectionString());
                return new UsedHelper(databases, System.IO.Path.GetDirectoryName(file)!, file, Pooled: true);
            }
            case Helper.GuidedSetupTenantDatabases:
            {
                var databases = new GuidedSetupServiceTests.TenantDatabases();
                await using var db = await databases.CreateAsync(tenant, Ct, withHooks: false);
                db.AppUsers.Add(User(Guid.NewGuid()));
                await db.SaveChangesAsync(Ct);
                Assert.Equal(1, await db.AppUsers.CountAsync(Ct));
                var file = DataSource(db.Database.GetConnectionString());
                return new UsedHelper(databases, System.IO.Path.GetDirectoryName(file)!, file, Pooled: true);
            }
            case Helper.CentralTestDatabase:
            {
                var central = new CentralTestDatabase();
                central.AddTenant("pool-" + tenant.ToString("N")[..8]);
                var file = DataSource(central.ConnectionString);
                return new UsedHelper(central, file, file, Pooled: false);
            }
            case Helper.RecordingTenantDbFactory:
            {
                var tenants = new RecordingTenantDbFactory();
                tenants.CreateTenantDatabase(tenant);
                await using (var db = await tenants.CreateAsync(tenant, Ct))
                    Assert.Equal(0, await db.AppUsers.CountAsync(Ct));
                var file = DataSource(tenants.ConnectionStringFor(tenant));
                return new UsedHelper(tenants, System.IO.Path.GetDirectoryName(file)!, file, Pooled: false);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(helper), helper, null);
        }
    }

    /// <summary>
    /// Puts an open pooled connection back into the state Microsoft.Data.Sqlite 8.0.10 gives it inside
    /// <c>SqliteConnectionInternal.Activate</c>, between <c>_active = true</c> and <c>_outerConnection.SetTarget(owner)</c>:
    /// active, owner not yet recorded. A pool cleared in that window takes the connection for leaked and disposes its
    /// native handle. The window is two instructions long, so the test holds it open instead of racing for it. Returns
    /// the action that finishes the open.
    /// </summary>
    private static Action HoldInsideOpen(SqliteConnection connection)
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var inner = typeof(SqliteConnection).GetField("_innerConnection", Instance)?.GetValue(connection)
            ?? throw new InvalidOperationException("SqliteConnection._innerConnection is missing; re-check the open window this test models.");
        var owner = inner.GetType().GetField("_outerConnection", Instance)?.GetValue(inner) as WeakReference<SqliteConnection?>
            ?? throw new InvalidOperationException("SqliteConnectionInternal._outerConnection is missing; re-check the open window this test models.");
        var leaked = inner.GetType().GetProperty("Leaked", Instance)
            ?? throw new InvalidOperationException("SqliteConnectionInternal.Leaked is missing; re-check the open window this test models.");
        Assert.NotNull(inner.GetType().GetField("_pool", Instance)?.GetValue(inner));

        owner.SetTarget(null);
        Assert.True((bool)leaked.GetValue(inner)!, "The modelled open window must be the state the pool treats as leaked.");
        return () => owner.SetTarget(connection);
    }

    private static object? Handle(TenantDbContext db) => ((SqliteConnection)db.Database.GetDbConnection()).Handle;

    private static string DataSource(string? connectionString) =>
        Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);

    private static bool IsHeldOpen(string file)
    {
        try
        {
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static AppUser User(Guid id) => new()
    {
        Id = id,
        Email = id.ToString("N") + "@example.test",
        PasswordHash = "hash",
        FullName = "Test User",
        Role = UserRole.Owner,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
