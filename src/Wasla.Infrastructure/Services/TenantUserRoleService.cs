using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Every change runs in one serializable transaction that first re-reads the acting user, so an actor who was demoted,
/// deactivated, deleted or given a new security stamp between the request's session check and the write changes
/// nothing. On SQL Server the serializable read locks also keep two concurrent changes from each relying on the other's
/// Owner: one waits for the other, or is chosen as the deadlock victim and fails without writing.
/// </summary>
/// <remarks>
/// Creating a user (WAS-116) also takes the tenant database's creation lock first in its transaction, so simultaneous
/// creates run one at a time and the later ones find the email instead of deadlocking at their inserts. A create chosen
/// as a deadlock victim is tried again from the start, and a duplicate key on the email index is answered as a
/// duplicate email.
/// </remarks>
public sealed class TenantUserRoleService : ITenantUserRoleService
{
    /// <summary>How many times one user creation is tried in all when SQL Server picks it as a deadlock victim.</summary>
    internal const int MaxCreateAttempts = 3;

    /// <summary>
    /// The application lock that puts the user creations of one tenant database in a queue. Application locks belong to
    /// one database, so this is one queue per tenant.
    /// </summary>
    internal const string CreateLockResource = "Wasla.TenantUsers.Create";

    private const int DeadlockVictim = 1205;
    private const string EmailIndex = "IX_AppUsers_Email";
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private static readonly TimeSpan CreateLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly ILogger<TenantUserRoleService> _logger;

    public TenantUserRoleService(
        ITenantDbContextFactory dbFactory,
        IPasswordPolicy passwordPolicy,
        ILogger<TenantUserRoleService> logger)
    {
        _dbFactory = dbFactory;
        _passwordPolicy = passwordPolicy;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TenantUserSummaryDto>> ListUsersAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        return await db.AppUsers
            .AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new TenantUserSummaryDto(
                u.Id,
                u.Email,
                u.FullName,
                u.Role,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TenantUserSummaryDto?> GetUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        return await db.AppUsers
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new TenantUserSummaryDto(
                u.Id,
                u.Email,
                u.FullName,
                u.Role,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TenantUserMutationResult> CreateUserAsync(
        Guid tenantId,
        TenantUserActor actor,
        TenantUserCreateCommand command,
        CancellationToken ct)
    {
        if (!IsAllowedRole(command.Role))
            return TenantUserMutationResult.InvalidRole();

        var passwordResult = _passwordPolicy.Validate(command.Password);
        if (!passwordResult.IsValid)
            return TenantUserMutationResult.InvalidPassword(passwordResult.Errors);

        // BCrypt is slow on purpose. Hashed before the transaction, so no lock waits for it, and only once, however many
        // attempts the create takes.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(command.Password);

        // The tenant contexts use EF's default execution strategy, which retries nothing and reports a deadlock as an
        // InvalidOperationException around the SqlException; this loop is the only retry. SQL Server has rolled a
        // deadlock victim back, so nothing was written, and the next attempt reads the actor and the email again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryCreateUserAsync(tenantId, actor, command, passwordHash, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxCreateAttempts && IsDeadlockVictim(ex))
            {
                _logger.LogWarning(
                    "Tenant user creation in tenant {TenantId} was chosen as a deadlock victim (SQL Server error {SqlErrorNumber}); trying again, attempt {Attempt} of {MaxAttempts}.",
                    tenantId,
                    DeadlockVictim,
                    attempt + 1,
                    MaxCreateAttempts);
            }
        }
    }

    private async Task<TenantUserMutationResult> TryCreateUserAsync(
        Guid tenantId,
        TenantUserActor actor,
        TenantUserCreateCommand command,
        string passwordHash,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        await AcquireCreateLockAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserMutationResult.ActorNotAuthorized();

        var email = NormalizeEmail(command.Email);
        var emailExists = await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Email == email, ct)
            .ConfigureAwait(false);
        if (emailExists)
            return TenantUserMutationResult.DuplicateEmail();

        var now = DateTime.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = command.FullName.Trim(),
            Role = command.Role,
            IsActive = command.IsActive,
            PasswordHash = passwordHash,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AppUsers.Add(user);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsEmailConflict(ex))
        {
            // A row with this email that the check did not see. Disposing the transaction rolls it back.
            return TenantUserMutationResult.DuplicateEmail();
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserMutationResult.Created(user.Id);
    }

    public async Task<TenantUserMutationResult> UpdateUserAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        TenantUserUpdateCommand command,
        CancellationToken ct)
    {
        if (!IsAllowedRole(command.Role))
            return TenantUserMutationResult.InvalidRole();

        var changesPassword = !string.IsNullOrWhiteSpace(command.NewPassword);
        if (changesPassword)
        {
            var passwordResult = _passwordPolicy.Validate(command.NewPassword!);
            if (!passwordResult.IsValid)
                return TenantUserMutationResult.InvalidPassword(passwordResult.Errors);
        }

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserMutationResult.ActorNotAuthorized();

        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserMutationResult.UserNotFound();

        var changesRole = user.Role != command.Role;
        var changesActive = user.IsActive != command.IsActive;
        if (userId == actor.UserId && (changesRole || changesActive))
            return TenantUserMutationResult.SelfChangeNotAllowed(userId);

        if (user.Role == UserRole.Owner
            && command.Role != UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserMutationResult.LastOwnerWouldBeRemoved(userId);
        }

        if (!command.IsActive
            && user.IsActive
            && user.Role == UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserMutationResult.LastOwnerWouldBeRemoved(userId);
        }

        user.FullName = command.FullName.Trim();
        user.Role = command.Role;
        user.IsActive = command.IsActive;
        if (changesPassword)
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(command.NewPassword);
        if (changesRole || changesActive || changesPassword)
            user.SecurityStamp = Guid.NewGuid();
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserMutationResult.Updated(user.Id);
    }

    public async Task<TenantUserRoleUpdateResult> ChangeRoleAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        UserRole role,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserRoleUpdateResult.ActorNotAuthorized();

        if (userId == actor.UserId)
            return TenantUserRoleUpdateResult.SelfChangeNotAllowed();

        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (user.Role == UserRole.Owner
            && role != UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        if (user.Role != role)
            user.SecurityStamp = Guid.NewGuid();
        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    public async Task<TenantUserRoleUpdateResult> SetActiveAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        bool isActive,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserRoleUpdateResult.ActorNotAuthorized();

        if (userId == actor.UserId)
            return TenantUserRoleUpdateResult.SelfChangeNotAllowed();

        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (!isActive
            && user.IsActive
            && user.Role == UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        if (user.IsActive != isActive)
            user.SecurityStamp = Guid.NewGuid();
        user.IsActive = isActive;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    public async Task<TenantUserRoleUpdateResult> RemoveAsync(
        Guid tenantId,
        TenantUserActor actor,
        Guid userId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        if (!await IsAuthorizedActorAsync(db, actor, ct).ConfigureAwait(false))
            return TenantUserRoleUpdateResult.ActorNotAuthorized();

        if (userId == actor.UserId)
            return TenantUserRoleUpdateResult.SelfChangeNotAllowed();

        var user = await FindUserAsync(db, userId, ct).ConfigureAwait(false);
        if (user is null)
            return TenantUserRoleUpdateResult.UserNotFound();

        if (user.IsActive
            && user.Role == UserRole.Owner
            && !await HasAnotherActiveOwnerAsync(db, userId, ct).ConfigureAwait(false))
        {
            return TenantUserRoleUpdateResult.LastOwnerWouldBeRemoved();
        }

        db.AppUsers.Remove(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return TenantUserRoleUpdateResult.Updated();
    }

    private static Task<IDbContextTransaction> BeginAsync(TenantDbContext db, CancellationToken ct) =>
        SerializableTransaction.BeginAsync(db.Database, ct);

    /// <summary>
    /// Takes <see cref="CreateLockResource"/>: an exclusive SQL Server application lock owned by the create's
    /// transaction, released when it commits or rolls back. Without it, simultaneous creates of one email all passed the
    /// email check under shared key-range locks and then deadlocked at their inserts. It is taken first, while the
    /// transaction holds no other lock, so waiting for it cannot be part of a deadlock. A lock not granted within
    /// <see cref="CreateLockTimeout"/> fails the request.
    /// </summary>
    private static async Task AcquireCreateLockAsync(TenantDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
        {
            // SQLite backs single-process unit tests only, and runs the serializable transactions one after the other.
            if (string.Equals(db.Database.ProviderName, SqliteProvider, StringComparison.Ordinal))
                return;
            throw new InvalidOperationException("Tenant user creation requires the tenant database on SQL Server.");
        }

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction()
                              ?? throw new InvalidOperationException("The creation lock is owned by a transaction; begin one first.");
        command.CommandText =
            "DECLARE @result int; " +
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', " +
            "@LockOwner = N'Transaction', @LockTimeout = @timeout; " +
            "SELECT @result;";

        var resource = command.CreateParameter();
        resource.ParameterName = "@resource";
        resource.DbType = DbType.String;
        resource.Size = 255;
        resource.Value = CreateLockResource;
        command.Parameters.Add(resource);

        var timeout = command.CreateParameter();
        timeout.ParameterName = "@timeout";
        timeout.DbType = DbType.Int32;
        timeout.Value = (int)CreateLockTimeout.TotalMilliseconds;
        command.Parameters.Add(timeout);

        var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (result < 0)
            throw new InvalidOperationException($"The tenant user creation lock was not granted (sp_getapplock result {result}).");
    }

    /// <summary>True when SQL Server chose the attempt as a deadlock victim (error 1205), at any depth of the exception.</summary>
    private static bool IsDeadlockVictim(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql.Number == DeadlockVictim;
        }

        return false;
    }

    /// <summary>
    /// True only for a duplicate key on <c>IX_AppUsers_Email</c>: SQL Server error 2601 or 2627 naming that index. Any
    /// other duplicate key, constraint or database error is not a duplicate email and fails the caller.
    /// </summary>
    internal static bool IsEmailConflict(DbUpdateException exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number is 2601 or 2627
                    && sql.Errors[0].Message.Contains($"'{EmailIndex}'", StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// The actor must still be an active Owner (the role of the <c>CanManageTenantUsers</c> policy) with the security
    /// stamp their session was validated with.
    /// </summary>
    private static async Task<bool> IsAuthorizedActorAsync(
        TenantDbContext db,
        TenantUserActor actor,
        CancellationToken ct) =>
        actor.UserId != Guid.Empty
        && actor.SecurityStamp != Guid.Empty
        && await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id == actor.UserId
                           && u.IsActive
                           && u.Role == UserRole.Owner
                           && u.SecurityStamp == actor.SecurityStamp, ct)
            .ConfigureAwait(false);

    private static async Task<AppUser?> FindUserAsync(
        TenantDbContext db,
        Guid userId,
        CancellationToken ct) =>
        await db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);

    private static async Task<bool> HasAnotherActiveOwnerAsync(
        TenantDbContext db,
        Guid userId,
        CancellationToken ct) =>
        await db.AppUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id != userId && u.IsActive && u.Role == UserRole.Owner, ct)
            .ConfigureAwait(false);

    private static bool IsAllowedRole(UserRole role) =>
        role is UserRole.Owner or UserRole.Manager or UserRole.Kitchen or UserRole.Cashier or UserRole.Viewer;

    private static string NormalizeEmail(string email) => email.Trim();
}
