using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-42: the <c>create-user</c> handler. It adds a user to an existing, active tenant only after the slug, the email,
/// the role (by name), the Web panel's password policy, the tenant and the tenant's existing emails were checked, stores
/// only a BCrypt hash, and its messages never include the password, the hash or an exception message. CentralDb and the tenant
/// database are in-memory SQLite with fake data; the tenant database is handed in through the handler's seam.
/// </summary>
[Collection(nameof(CliConsoleCollection))]
public sealed class CliCreateUserTests
{
    private const string FakePassword = "Fake-password-1";
    private const string OtherPassword = "Other-fake-password-2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Owner", UserRole.Owner)]
    [InlineData("manager", UserRole.Manager)]
    [InlineData("KITCHEN", UserRole.Kitchen)]
    [InlineData(" Cashier ", UserRole.Cashier)]
    [InlineData("Viewer", UserRole.Viewer)]
    public async Task CreatesAnActiveUser_InTheSelectedTenant_WithTheRoleAndOnlyABcryptHash(string roleInput, UserRole expectedRole)
    {
        await using var central = await CentralAsync();
        var demo = SeedTenant(central.Db, "demo", isActive: true);
        SeedTenant(central.Db, "other", isActive: true);
        await central.Db.SaveChangesAsync(Ct);
        await using var tenantDb = await TenantConnectionAsync();
        using var host = central.Host();
        var opened = new List<Guid>();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "  kitchen@example.com ", FakePassword, roleInput, "  Demo User ", Ct,
            (tenant, _) => { opened.Add(tenant.Id); return Task.FromResult(OpenTenant(tenantDb)); }));

        Assert.Equal(0, code);
        Assert.Equal([demo.Id], opened);
        Assert.Contains($"✓ User kitchen@example.com created (role: {expectedRole}).", output, StringComparison.Ordinal);

        var user = Assert.Single(await UsersAsync(tenantDb));
        Assert.Equal("kitchen@example.com", user.Email);
        Assert.Equal("Demo User", user.FullName);
        Assert.Equal(expectedRole, user.Role);
        Assert.True(user.IsActive);
        Assert.Null(user.BranchId);
        Assert.Null(user.LastLoginAt);
        Assert.NotEqual(Guid.Empty, user.SecurityStamp);
        Assert.StartsWith("$2", user.PasswordHash, StringComparison.Ordinal);
        Assert.True(BCrypt.Net.BCrypt.Verify(FakePassword, user.PasswordHash));
        Assert.DoesNotContain(FakePassword, output, StringComparison.Ordinal);
        Assert.DoesNotContain(user.PasswordHash, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEightCharacterPassword_AndNoName_AreAccepted()
    {
        await using var central = await CentralAsync();
        SeedTenant(central.Db, "demo", isActive: true);
        await central.Db.SaveChangesAsync(Ct);
        await using var tenantDb = await TenantConnectionAsync();
        using var host = central.Host();

        var (code, _) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "viewer@example.com", "Fake-pw1", "Viewer", null, Ct,
            (_, _) => Task.FromResult(OpenTenant(tenantDb))));

        Assert.Equal(0, code);
        var user = Assert.Single(await UsersAsync(tenantDb));
        Assert.Equal(string.Empty, user.FullName);
        Assert.True(BCrypt.Net.BCrypt.Verify("Fake-pw1", user.PasswordHash));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("100")]
    [InlineData("Staff")]
    [InlineData("Owner,Manager")]
    [InlineData("Viewer, Kitchen")]
    [InlineData("Admin")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ARoleThatIsNotOneOfTheFiveNames_IsRefused_BeforeAnyDatabaseIsOpened(string role)
    {
        // A host without services: any database access would end in another message and exit code.
        using var host = new HostBuilder().Build();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "user@example.com", FakePassword, role, null, Ct, UnusedTenant));

        Assert.Equal(2, code);
        Assert.Contains("Role must be one of: Owner, Manager, Kitchen, Cashier, Viewer.", output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, output, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleNames_AreTheOnlyAcceptedRoleValues()
    {
        // Enum parsing would turn a list into a different role, and a number into any role.
        Assert.True(Enum.TryParse<UserRole>("Owner,Manager", ignoreCase: true, out var combined));
        Assert.Equal(UserRole.Kitchen, combined);

        Assert.False(CliCommands.TryParseAssignableUserRole("Owner,Manager", out _));
        Assert.False(CliCommands.TryParseAssignableUserRole("1", out _));
        Assert.False(CliCommands.TryParseAssignableUserRole(null, out _));
        Assert.True(CliCommands.TryParseAssignableUserRole(" owner ", out var owner));
        Assert.Equal(UserRole.Owner, owner);
    }

    [Theory]
    [InlineData("", "Password is required.")]
    [InlineData("        ", "Password is required.")]
    [InlineData("Fake-1!", "Password must be at least 8 characters.")]
    public async Task APasswordTheWebPanelRefuses_IsRefused_NeverPrinted_AndNoDatabaseIsOpened(string password, string expected)
    {
        using var host = new HostBuilder().Build();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "user@example.com", password, "Owner", null, Ct, UnusedTenant));

        Assert.Equal(2, code);
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.Contains("No user was created.", output, StringComparison.Ordinal);
        if (password.Trim().Length > 0)
            Assert.DoesNotContain(password, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public async Task AnInvalidEmail_IsRefused_BeforeAnyDatabaseIsOpened(string email)
    {
        using var host = new HostBuilder().Build();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", email, FakePassword, "Viewer", null, Ct, UnusedTenant));

        Assert.Equal(2, code);
        Assert.Contains("Email must be a valid email address.", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other", "Tenant not found for slug.")]
    [InlineData("paused", "Tenant is inactive.")]
    public async Task AnUnknownOrInactiveTenant_IsRefused_WithoutOpeningATenantDatabase(string slug, string expected)
    {
        await using var central = await CentralAsync();
        SeedTenant(central.Db, "demo", isActive: true);
        SeedTenant(central.Db, "paused", isActive: false);
        await central.Db.SaveChangesAsync(Ct);
        using var host = central.Host();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, slug, "user@example.com", FakePassword, "Owner", null, Ct, UnusedTenant));

        Assert.Equal(2, code);
        Assert.Contains(expected, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingEmail_IsRefused_AndTheExistingUserIsUnchanged()
    {
        await using var central = await CentralAsync();
        SeedTenant(central.Db, "demo", isActive: true);
        await central.Db.SaveChangesAsync(Ct);
        await using var tenantDb = await TenantConnectionAsync();
        var existing = await SeedUserAsync(tenantDb, "kitchen@example.com", UserRole.Viewer, OtherPassword);
        using var host = central.Host();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "  kitchen@example.com ", FakePassword, "Owner", "Someone Else", Ct,
            (_, _) => Task.FromResult(OpenTenant(tenantDb))));

        Assert.Equal(2, code);
        Assert.Contains("A user with this email already exists.", output, StringComparison.Ordinal);
        var user = Assert.Single(await UsersAsync(tenantDb));
        Assert.Equal(existing.Id, user.Id);
        Assert.Equal(UserRole.Viewer, user.Role);
        Assert.Equal(existing.FullName, user.FullName);
        Assert.Equal(existing.SecurityStamp, user.SecurityStamp);
        Assert.Equal(existing.PasswordHash, user.PasswordHash);
    }

    [Fact]
    public async Task AnEmailAddedAfterTheCheck_IsRefusedByTheUniqueIndex_WithoutTheDatabaseMessage()
    {
        await using var central = await CentralAsync();
        SeedTenant(central.Db, "demo", isActive: true);
        await central.Db.SaveChangesAsync(Ct);
        await using var tenantDb = await TenantConnectionAsync();
        using var host = central.Host();
        var otherWriter = Guid.NewGuid();

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "kitchen@example.com", FakePassword, "Kitchen", null, Ct,
            (_, _) =>
            {
                var db = OpenTenant(tenantDb);
                // Another writer stores the same email between the duplicate check and this save.
                db.SavingChanges += (_, _) => InsertUser(tenantDb, otherWriter, "kitchen@example.com", OtherPassword);
                return Task.FromResult(db);
            }));

        Assert.Equal(3, code);
        // Exactly the handler's own line: neither the save error nor SQLite's inner constraint message is printed.
        Assert.Equal("Database operation failed. No user was created.", output.Trim());
        var user = Assert.Single(await UsersAsync(tenantDb));
        Assert.Equal(otherWriter, user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(OtherPassword, user.PasswordHash));
    }

    public static TheoryData<string, int, string> TenantDatabaseFailures => new()
    {
        { "update", 3, "Database operation failed. No user was created." },
        { "decrypt", 1, "create-user failed (CryptographicException). No user was created." },
        { "connection-string", 1, "create-user failed (ArgumentException). No user was created." },
    };

    [Theory]
    [MemberData(nameof(TenantDatabaseFailures))]
    public async Task ATenantDatabaseFailure_PrintsNoExceptionMessage_AndCreatesNothing(string failure, int expectedCode, string expected)
    {
        const string Secret = "Fake-db-secret-7Q";
        await using var central = await CentralAsync();
        SeedTenant(central.Db, "demo", isActive: true);
        await central.Db.SaveChangesAsync(Ct);
        using var host = central.Host();
        Exception error = failure switch
        {
            "update" => new DbUpdateException($"Server=fake;User Id=sa;Password={Secret}"),
            "decrypt" => new CryptographicException($"Key {Secret} does not match."),
            _ => new ArgumentException($"Format of the initialization string near 'Password={Secret}' is invalid.")
        };

        var (code, output) = await CaptureAsync(() => CliCommands.CreateUserAsync(
            host, "demo", "kitchen@example.com", FakePassword, "Kitchen", null, Ct,
            (_, _) => throw error));

        Assert.Equal(expectedCode, code);
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, output, StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static Task<TenantDbContext> UnusedTenant(Tenant tenant, CancellationToken ct) =>
        throw new InvalidOperationException("No tenant database may be opened.");

    private static Tenant SeedTenant(CentralDbContext central, string slug, bool isActive)
    {
        var tenant = new Tenant
        {
            Name = "Fake " + slug,
            Slug = slug,
            PrimaryDomain = slug + ".app.wasla.local",
            DatabaseName = "Wasla_Fake_" + slug,
            EncryptedConnectionString = "not-used-in-test",
            EncryptionKeyVersion = 1,
            IsActive = isActive
        };
        central.Tenants.Add(tenant);
        return tenant;
    }

    private static async Task<CentralTestDatabase> CentralAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var db = new CentralDbContext(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new CentralTestDatabase(connection, db);
    }

    private sealed class CentralTestDatabase(SqliteConnection connection, CentralDbContext db) : IAsyncDisposable
    {
        public CentralDbContext Db { get; } = db;

        /// <summary>A host whose CentralDb is this database. It has no secret manager, so only the test's seam opens a tenant.</summary>
        public IHost Host() => new HostBuilder()
            .ConfigureServices(services => services.AddDbContext<CentralDbContext>(options => options.UseSqlite(connection)))
            .Build();

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<SqliteConnection> TenantConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE AppUsers (
                Id TEXT NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
                Email TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                FullName TEXT NOT NULL,
                Role INTEGER NOT NULL,
                BranchId TEXT NULL,
                IsActive INTEGER NOT NULL,
                SecurityStamp TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                LastLoginAt TEXT NULL
            );

            CREATE UNIQUE INDEX IX_AppUsers_Email ON AppUsers (Email);
            """;
        await command.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    private static TenantDbContext OpenTenant(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options);

    private static async Task<AppUser> SeedUserAsync(SqliteConnection connection, string email, UserRole role, string password)
    {
        await using var db = OpenTenant(connection);
        var user = new AppUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FullName = "Existing Fake User",
            Role = role,
            IsActive = true
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    private static void InsertUser(SqliteConnection connection, Guid id, string email, string password)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppUsers (Id, Email, PasswordHash, FullName, Role, BranchId, IsActive, SecurityStamp, CreatedAt, UpdatedAt, LastLoginAt)
            VALUES ($id, $email, $hash, 'Other Fake Writer', 5, NULL, 1, $stamp, '2026-01-01 00:00:00', '2026-01-01 00:00:00', NULL);
            """;
        command.Parameters.AddWithValue("$id", id.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$hash", BCrypt.Net.BCrypt.HashPassword(password));
        command.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString().ToUpperInvariant());
        command.ExecuteNonQuery();
    }

    private static async Task<List<AppUser>> UsersAsync(SqliteConnection connection)
    {
        await using var db = OpenTenant(connection);
        return await db.AppUsers.AsNoTracking().ToListAsync(Ct);
    }

    private static async Task<(int Code, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var code = await action();
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
