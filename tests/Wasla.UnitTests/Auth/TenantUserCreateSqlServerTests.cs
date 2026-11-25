using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Contact;
using Wasla.UnitTests.Orders;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// WAS-116: tenant user creation on a real SQL Server (a throwaway database migrated with this build's tenant
/// migrations, see <see cref="SqlServerTenantDatabase"/>). Every caller uses its own service, DbContext and connection
/// pool, as separate Web instances would. SQLite runs the serializable transactions one after the other and has no
/// application locks, so only SQL Server shows these races. Before WAS-116 the simultaneous creates, the held create
/// and the deadlock victim each ended in a SQL Server deadlock error (HTTP 500 in the Web), and the duplicate key in an
/// unhandled DbUpdateException.
/// </summary>
public sealed class TenantUserCreateSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    // The databases of SqlServerTenantDatabase are selected by the fixture, not by this id.
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private const string FakePassword = "Fake-create-password-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SimultaneousCreatesOfOneEmail_FromSeparateInstances_StoreOneUser_AndAnswerTheRestWithDuplicateEmail()
    {
        var owner = await SeedOwnerAsync();
        var email = NewEmail();
        const int callers = 8;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new CapturingLogger<TenantUserRoleService>();

        var creates = Enumerable.Range(0, callers)
            .Select(i => (Index: i, Service: Service($"Create-{i}", log: log)))
            .Select(caller => Task.Run(async () =>
            {
                await go.Task;
                // Half the callers type the email in upper case: SQL Server compares emails case-insensitively.
                var typed = caller.Index % 2 == 0 ? email : email.ToUpperInvariant();
                return await caller.Service.CreateUserAsync(
                    TenantId, Actor(owner), Command(typed, $"Caller {caller.Index}"), Ct);
            }, Ct))
            .ToList();
        go.SetResult();
        var results = await Task.WhenAll(creates).WaitAsync(Deadline, Ct);

        var created = Assert.Single(results, r => r.Outcome == TenantUserRoleUpdateOutcome.Created);
        Assert.Equal(callers - 1, results.Count(r => r.Outcome == TenantUserRoleUpdateOutcome.DuplicateEmail));
        var stored = Assert.Single(await UsersWithEmailAsync(email));
        Assert.Equal(created.UserId, stored.Id);
        // The creates waited for each other: none was chosen as a deadlock victim and tried again.
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ACreateOfTheSameEmail_WaitsForTheCreateBeforeIt_AndThenAnswersDuplicateEmail()
    {
        var owner = await SeedOwnerAsync();
        var email = NewEmail();
        var reached = new TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first create is held right before its insert, inside its transaction.
        var first = Service("Held", new BeforeUserInsert(async (insert, ct) =>
        {
            reached.TrySetResult(await SessionIdAsync(insert, ct));
            await release.Task.WaitAsync(Deadline, ct);
        })).CreateUserAsync(TenantId, Actor(owner), Command(email, "First"), Ct);
        var firstSession = await reached.Task.WaitAsync(Deadline, Ct);

        // The second create waits at the creation lock, before it reads its actor or the email.
        var second = Service("Waiting").CreateUserAsync(TenantId, Actor(owner), Command(email.ToUpperInvariant(), "Second"), Ct);
        var waitResource = await WaitUntilBlockedAsync(firstSession, second);
        Assert.StartsWith("APPLICATION", waitResource, StringComparison.Ordinal);
        Assert.Contains($"[{TenantUserRoleService.CreateLockResource}]", waitResource, StringComparison.Ordinal);
        release.SetResult();

        Assert.Equal(TenantUserRoleUpdateOutcome.Created, (await first.WaitAsync(Deadline, Ct)).Outcome);
        Assert.Equal(TenantUserRoleUpdateOutcome.DuplicateEmail, (await second.WaitAsync(Deadline, Ct)).Outcome);
        var stored = Assert.Single(await UsersWithEmailAsync(email));
        Assert.Equal("First", stored.FullName);
    }

    [Fact]
    public async Task ACreateChosenAsTheDeadlockVictim_IsTriedAgainFromTheStart_AndThenFindsItsActorDemoted()
    {
        var owner = await SeedOwnerAsync();
        var email = NewEmail();
        var factory = new InstanceFactory(database.ConnectionString("Victim"), interceptor: null);
        var log = new CapturingLogger<TenantUserRoleService>();
        var service = new TenantUserRoleService(factory, new DefaultPasswordPolicy(), log);

        // Another user-management change on another instance: it has read every user, as the last-Owner check does,
        // and is about to demote the acting Owner. Its higher deadlock priority makes the create the victim.
        await using var other = database.Open("OtherChange");
        await other.Database.OpenConnectionAsync(Ct);
        await other.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY HIGH;", Ct);
        await using var transaction = await other.Database.BeginTransactionAsync(Ct);
        await other.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [AppUsers] WITH (HOLDLOCK)").SingleAsync(Ct);
        var otherSession = await other.Database.SqlQuery<short>($"SELECT @@SPID AS [Value]").SingleAsync(Ct);

        // The create reads its actor, then waits at its insert for the other change's read.
        var create = service.CreateUserAsync(TenantId, Actor(owner), Command(email, "Victim"), Ct);
        await WaitUntilBlockedAsync(otherSession, create);

        // The demotion waits for the create's read of its actor: a deadlock, which SQL Server ends by rolling the
        // create back. The other change then commits.
        await other.Database
            .ExecuteSqlInterpolatedAsync(
                $"UPDATE [AppUsers] SET [Role] = {(int)UserRole.Manager}, [SecurityStamp] = NEWID() WHERE [Id] = {owner.Id}",
                Ct)
            .WaitAsync(Deadline, Ct);
        await transaction.CommitAsync(Ct);
        await other.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY NORMAL;", Ct);

        var result = await create.WaitAsync(Deadline, Ct);

        // The deadlocked attempt and the one after it, each with its own context, which found the actor demoted.
        Assert.Equal(TenantUserRoleUpdateOutcome.ActorNotAuthorized, result.Outcome);
        Assert.Equal(2, factory.Created);
        Assert.Empty(await UsersWithEmailAsync(email));
        var retry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, retry.Level);
        Assert.Contains("1205", retry.Text, StringComparison.Ordinal);
        Assert.Contains($"attempt 2 of {TenantUserRoleService.MaxCreateAttempts}", retry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(email, retry.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FakePassword, retry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateKeyOnTheEmailIndexAtTheInsert_IsAnsweredWithDuplicateEmail_AndStoresNothing()
    {
        var owner = await SeedOwnerAsync();
        var email = NewEmail();

        // The same email written in the create's own transaction right before its insert stands in for a row the
        // create's check did not see: SQL Server then refuses the insert with a duplicate key on IX_AppUsers_Email.
        var service = Service("DuplicateKey", new BeforeUserInsert(async (insert, ct) =>
        {
            await using var duplicate = insert.Connection!.CreateCommand();
            duplicate.Transaction = insert.Transaction;
            duplicate.CommandText = """
                INSERT INTO [AppUsers] ([Id], [Email], [PasswordHash], [FullName], [Role], [IsActive], [SecurityStamp], [CreatedAt], [UpdatedAt])
                VALUES (NEWID(), @email, N'not-a-real-hash', N'Stand-in', 5, 1, NEWID(), SYSUTCDATETIME(), SYSUTCDATETIME());
                """;
            var parameter = duplicate.CreateParameter();
            parameter.ParameterName = "@email";
            parameter.Value = email.ToUpperInvariant();
            duplicate.Parameters.Add(parameter);
            await duplicate.ExecuteNonQueryAsync(ct);
        }));

        var result = await service.CreateUserAsync(TenantId, Actor(owner), Command(email, "Refused"), Ct);

        Assert.Equal(TenantUserRoleUpdateOutcome.DuplicateEmail, result.Outcome);
        // The create's transaction, the stand-in row included, was rolled back.
        Assert.Empty(await UsersWithEmailAsync(email));
    }

    [Fact]
    public async Task TheEmailConflictCheck_CountsOnlyADuplicateKeyOnTheEmailIndex()
    {
        var existing = await SeedOwnerAsync();

        var sameEmail = await SaveFailureAsync(NewUser(Guid.NewGuid(), existing.Email.ToUpperInvariant()));
        var sameId = await SaveFailureAsync(NewUser(existing.Id, NewEmail()));

        Assert.True(TenantUserRoleService.IsEmailConflict(sameEmail));
        Assert.False(TenantUserRoleService.IsEmailConflict(sameId));
    }

    private TenantUserRoleService Service(
        string instance,
        IInterceptor? interceptor = null,
        ILogger<TenantUserRoleService>? log = null) =>
        new(
            new InstanceFactory(database.ConnectionString(instance), interceptor),
            new DefaultPasswordPolicy(),
            log ?? new CapturingLogger<TenantUserRoleService>());

    private static TenantUserActor Actor(AppUser user) => new(user.Id, user.SecurityStamp);

    private static TenantUserCreateCommand Command(string email, string fullName) =>
        new(email, fullName, UserRole.Kitchen, IsActive: true, FakePassword);

    private static string NewEmail() => $"{Guid.NewGuid():N}@create-race.test";

    private static AppUser NewUser(Guid id, string email, UserRole role = UserRole.Viewer) => new()
    {
        Id = id,
        Email = email,
        PasswordHash = "not-a-real-hash",
        FullName = "Create Test User",
        Role = role,
        IsActive = true
    };

    private async Task<AppUser> SeedOwnerAsync()
    {
        var owner = NewUser(Guid.NewGuid(), NewEmail(), UserRole.Owner);
        await using var db = database.Open();
        db.AppUsers.Add(owner);
        await db.SaveChangesAsync(Ct);
        return owner;
    }

    private async Task<DbUpdateException> SaveFailureAsync(AppUser user)
    {
        await using var db = database.Open();
        db.AppUsers.Add(user);
        return await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    /// <summary>Every stored user with this email, compared as SQL Server compares it (case-insensitively).</summary>
    private async Task<List<AppUser>> UsersWithEmailAsync(string email)
    {
        await using var db = database.Open();
        return await db.AppUsers.AsNoTracking().Where(u => u.Email == email).ToListAsync(Ct);
    }

    private static async Task<short> SessionIdAsync(DbCommand insert, CancellationToken ct)
    {
        await using var command = insert.Connection!.CreateCommand();
        command.Transaction = insert.Transaction;
        command.CommandText = "SELECT @@SPID";
        return Convert.ToInt16(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Waits until a request is blocked by <paramref name="blockingSession"/>, and returns what it waits for.</summary>
    private async Task<string> WaitUntilBlockedAsync(short blockingSession, Task waiter)
    {
        await using var inspector = database.Open("Inspector");
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            if (waiter.IsCompleted)
            {
                await waiter;
                Assert.Fail("The create finished instead of waiting for the other session.");
            }

            var waiting = await inspector.Database
                .SqlQuery<string>($"SELECT wait_resource AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {blockingSession}")
                .ToListAsync(Ct);
            if (waiting.Count > 0)
                return waiting[0];
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The create was never blocked by the other session.");
        return string.Empty;
    }

    /// <summary>The tenant contexts of one simulated Web instance (its own connection pool), counted.</summary>
    private sealed class InstanceFactory(string connectionString, IInterceptor? interceptor) : ITenantDbContextFactory
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
        {
            Interlocked.Increment(ref _created);
            var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connectionString);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            return Task.FromResult(new TenantDbContext(options.Options));
        }
    }

    /// <summary>Runs a step on the create's own connection and transaction right before its user insert.</summary>
    private sealed class BeforeUserInsert(Func<DbCommand, CancellationToken, Task> step) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsUserInsert(command))
                await step(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsUserInsert(command))
                await step(command, cancellationToken);
            return result;
        }

        private static bool IsUserInsert(DbCommand command) =>
            command.CommandText.Contains("INSERT INTO [AppUsers]", StringComparison.Ordinal);
    }
}
