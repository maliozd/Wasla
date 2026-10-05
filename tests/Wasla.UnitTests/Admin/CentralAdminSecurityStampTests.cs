using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// WAS-37. The Central Admin security stamp: when CentralDbContext rotates it, what the session validator reports, and
/// what login issues. The end-to-end cookie behavior is in <see cref="CentralAdminSessionRevalidationTests"/>.
/// </summary>
public sealed class CentralAdminSecurityStampTests : IDisposable
{
    private const string Email = "stamp-admin@wasla.test";
    private const string Password = "Stamp-Test-Password-42";

    private readonly CentralTestDatabase _central = new();
    private readonly Guid _adminId;
    private readonly Guid _initialStamp;

    public CentralAdminSecurityStampTests()
    {
        using var db = _central.CreateContext();
        var admin = new CentralAdminUser
        {
            Email = Email,
            NormalizedEmail = Email.ToUpperInvariant(),
            DisplayName = "Stamp",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4),
            IsActive = true
        };
        db.CentralAdminUsers.Add(admin);
        db.SaveChanges();
        _adminId = admin.Id;
        _initialStamp = admin.SecurityStamp;
    }

    public void Dispose() => _central.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Rotation --------------------------------------------------------------------------------------

    [Fact]
    public void NewAdmins_GetADistinctNonEmptyStamp()
    {
        Assert.NotEqual(Guid.Empty, _initialStamp);
        Assert.NotEqual(_initialStamp, new CentralAdminUser().SecurityStamp);
    }

    [Fact]
    public async Task PasswordChange_RotatesTheStamp()
    {
        await UpdateAsync(admin => admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword("Another-Password-1", workFactor: 4));

        Assert.NotEqual(_initialStamp, StoredStamp());
        Assert.NotEqual(Guid.Empty, StoredStamp());
    }

    [Fact]
    public void PasswordChange_RotatesTheStamp_WithSynchronousSaveChanges()
    {
        using (var db = _central.CreateContext())
        {
            db.CentralAdminUsers.Single(x => x.Id == _adminId).PasswordHash = "replaced-hash";
            db.SaveChanges();
        }

        Assert.NotEqual(_initialStamp, StoredStamp());
    }

    [Fact]
    public async Task DeactivationAndReactivation_EachRotateTheStamp()
    {
        await UpdateAsync(admin => admin.IsActive = false);
        var afterDeactivation = StoredStamp();
        await UpdateAsync(admin => admin.IsActive = true);
        var afterReactivation = StoredStamp();

        Assert.Equal(3, new HashSet<Guid> { _initialStamp, afterDeactivation, afterReactivation }.Count);
    }

    [Fact]
    public async Task LoginBookkeepingAndProfileEdits_DoNotRotateTheStamp()
    {
        await UpdateAsync(admin =>
        {
            admin.LastLoginAt = DateTime.UtcNow;
            admin.UpdatedAt = DateTime.UtcNow;
            admin.DisplayName = "Renamed";
            admin.IsActive = true; // unchanged value
        });

        Assert.Equal(_initialStamp, StoredStamp());
    }

    [Fact]
    public async Task AStampSetByTheCaller_IsKept()
    {
        var chosen = Guid.NewGuid();
        await UpdateAsync(admin =>
        {
            admin.PasswordHash = "replaced-hash";
            admin.SecurityStamp = chosen;
        });

        Assert.Equal(chosen, StoredStamp());
    }

    [Fact]
    public async Task UpdatingADetachedAdmin_RotatesTheStampEvenThoughEveryPropertyIsMarkedModified()
    {
        CentralAdminUser detached;
        await using (var read = _central.CreateContext())
            detached = await read.CentralAdminUsers.AsNoTracking().SingleAsync(x => x.Id == _adminId, Ct);

        detached.PasswordHash = "replaced-hash";
        await using (var db = _central.CreateContext())
        {
            db.CentralAdminUsers.Update(detached);
            await db.SaveChangesAsync(Ct);
        }

        Assert.NotEqual(_initialStamp, StoredStamp());
    }

    [Fact]
    public async Task OnlyTheChangedAccountIsRotated()
    {
        Guid otherId;
        Guid otherStamp;
        await using (var db = _central.CreateContext())
        {
            var other = new CentralAdminUser { Email = "o@wasla.test", NormalizedEmail = "O@WASLA.TEST", DisplayName = "O", PasswordHash = "h" };
            db.CentralAdminUsers.Add(other);
            await db.SaveChangesAsync(Ct);
            (otherId, otherStamp) = (other.Id, other.SecurityStamp);
        }

        await UpdateAsync(admin => admin.IsActive = false);

        await using var check = _central.CreateContext();
        Assert.Equal(otherStamp, (await check.CentralAdminUsers.AsNoTracking().SingleAsync(x => x.Id == otherId, Ct)).SecurityStamp);
    }

    // Session validator -----------------------------------------------------------------------------

    [Fact]
    public async Task Validator_ReportsTheAccountState()
    {
        Assert.Equal(CentralAdminSessionState.Valid, await ValidateAsync(_adminId, _initialStamp));
        Assert.Equal(CentralAdminSessionState.StampChanged, await ValidateAsync(_adminId, Guid.NewGuid()));
        Assert.Equal(CentralAdminSessionState.AccountNotFound, await ValidateAsync(Guid.NewGuid(), _initialStamp));

        await UpdateAsync(admin => admin.IsActive = false);
        Assert.Equal(CentralAdminSessionState.AccountInactive, await ValidateAsync(_adminId, StoredStamp()));
    }

    [Fact]
    public async Task Validator_NeverAcceptsAnEmptyStoredStamp()
    {
        await using (var db = _central.CreateContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE \"CentralAdminUsers\" SET \"SecurityStamp\" = '00000000-0000-0000-0000-000000000000'", Ct);

        Assert.Equal(CentralAdminSessionState.StampChanged, await ValidateAsync(_adminId, Guid.Empty));
    }

    [Fact]
    public async Task Validator_IssuesOneCentralDbQueryWithoutSecretColumns()
    {
        await using var db = _central.CreateContext(counted: true);
        _central.Counter.Reset();

        await new CentralAdminSessionValidator(db).ValidateAsync(_adminId, _initialStamp, Ct);

        var sql = Assert.Single(_central.Counter.Commands);
        Assert.DoesNotContain("\"PasswordHash\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Email\"", sql, StringComparison.Ordinal);
    }

    // Login -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_ReturnsTheCurrentStamp_AndDoesNotRotateIt()
    {
        var first = await LoginAsync();
        var second = await LoginAsync();

        Assert.True(first.Succeeded);
        Assert.Equal(_initialStamp, first.SecurityStamp);
        Assert.Equal(_initialStamp, second.SecurityStamp);
        Assert.Equal(_initialStamp, StoredStamp());
    }

    [Fact]
    public async Task Login_ReplacesAnEmptyStoredStampBeforeIssuingIt()
    {
        await using (var db = _central.CreateContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE \"CentralAdminUsers\" SET \"SecurityStamp\" = '00000000-0000-0000-0000-000000000000'", Ct);

        var result = await LoginAsync();

        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, result.SecurityStamp);
        Assert.Equal(result.SecurityStamp, StoredStamp());
    }

    [Fact]
    public async Task FailedLogins_IssueNoStamp()
    {
        var wrongPassword = await LoginAsync("wrong-password");
        await UpdateAsync(admin => admin.IsActive = false);
        var inactive = await LoginAsync();

        Assert.False(wrongPassword.Succeeded);
        Assert.Null(wrongPassword.SecurityStamp);
        Assert.False(inactive.Succeeded);
        Assert.Null(inactive.SecurityStamp);
    }

    // Helpers ----------------------------------------------------------------------------------------

    private async Task UpdateAsync(Action<CentralAdminUser> change)
    {
        await using var db = _central.CreateContext();
        change(await db.CentralAdminUsers.SingleAsync(x => x.Id == _adminId, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private Guid StoredStamp()
    {
        using var db = _central.CreateContext();
        return db.CentralAdminUsers.AsNoTracking().Single(x => x.Id == _adminId).SecurityStamp;
    }

    private async Task<CentralAdminSessionState> ValidateAsync(Guid adminId, Guid stamp)
    {
        await using var db = _central.CreateContext();
        return await new CentralAdminSessionValidator(db).ValidateAsync(adminId, stamp, Ct);
    }

    private async Task<CentralAdminLoginResult> LoginAsync(string password = Password)
    {
        await using var db = _central.CreateContext();
        return await new CentralAdminAuthService(db, NullLogger<CentralAdminAuthService>.Instance).ValidateAsync(Email, password, Ct);
    }
}
