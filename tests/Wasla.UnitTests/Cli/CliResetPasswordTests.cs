using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Cli;

public sealed class CliResetPasswordTests
{
    private const string OldPassword = "OldPassword123!";
    private const string NewPassword = "NewPassword123!";

    [Fact]
    public async Task CentralDryRun_ResolvesExistingUser_AndDoesNotUpdate()
    {
        await using var central = await CreateCentralAsync();
        var user = await SeedCentralUserAsync(central, "mehmet@example.com", "Mehmet Admin");
        var originalHash = user.PasswordHash;
        var reader = new ThrowingPasswordReader();

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "central",
            "  Mehmet@Example.com ",
            tenantSelector: null,
            dryRun: true,
            openTenant: (_, _) => throw new InvalidOperationException("Tenant database must not be opened."),
            reader,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, code);
        Assert.Contains("Dry run: password would be reset. No changes were written.", output);
        Assert.Contains("Scope: Central", output);
        Assert.Contains("User: m***@example.com", output);
        Assert.DoesNotContain("mehmet@", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mehmet Admin", output);
        Assert.DoesNotContain("Password updated successfully.", output);
        Assert.Equal(originalHash, (await ReloadCentralAsync(central, user.Id)).PasswordHash);
    }

    [Fact]
    public async Task CentralCommand_UnknownUser_IsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var seed = CreateCentral(connection))
            await seed.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddDbContext<CentralDbContext>(options => options.UseSqlite(connection)))
            .Build();

        var (code, output) = await CaptureAsync(() => CliCommands.ResetPasswordAsync(
            host,
            "central",
            "missing@example.com",
            tenantSelector: null,
            dryRun: true,
            TestContext.Current.CancellationToken,
            new ThrowingPasswordReader()));

        Assert.Equal(2, code);
        Assert.Contains("No matching user was found. No changes were made.", output);
        Assert.DoesNotContain("Password updated successfully.", output);
    }

    [Fact]
    public async Task CentralAmbiguousUser_IsRejected()
    {
        await using var central = await CreateCentralAsync();
        await central.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_CentralAdminUsers_NormalizedEmail\"",
            TestContext.Current.CancellationToken);
        var first = await SeedCentralUserAsync(central, "owner@example.com", "First");
        var second = await SeedCentralUserAsync(central, "Owner@example.com", "Second");
        var firstHash = first.PasswordHash;
        var secondHash = second.PasswordHash;

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "central",
            "owner@example.com",
            tenantSelector: null,
            dryRun: false,
            openTenant: (_, _) => throw new InvalidOperationException("Tenant database must not be opened."),
            new ScriptedPasswordReader(NewPassword, NewPassword),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, code);
        Assert.Contains("More than one matching user was found. No changes were made.", output);
        Assert.Equal(firstHash, (await ReloadCentralAsync(central, first.Id)).PasswordHash);
        Assert.Equal(secondHash, (await ReloadCentralAsync(central, second.Id)).PasswordHash);
    }

    [Fact]
    public async Task CentralValidPassword_ChangesHash_AndKeepsIdentityAndMembership()
    {
        await using var central = await CreateCentralAsync();
        var tenant = SeedTenant(central, "Mengen Lokantası", "mengen");
        var user = await SeedCentralUserAsync(central, "mehmet@example.com", "Mehmet Admin");
        var membership = new TenantMembership
        {
            TenantId = tenant.Id,
            PlanCode = "starter",
            Status = MembershipStatus.Active,
            BillingPeriod = "Monthly",
            StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            OwnerEmail = "mehmet@example.com"
        };
        central.TenantMemberships.Add(membership);
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        var other = await SeedCentralUserAsync(central, "other@example.com", "Other Admin");
        var createdAt = user.CreatedAt;
        var lastLogin = user.LastLoginAt;
        var otherHash = other.PasswordHash;
        await using var promptOutput = new StringWriter();
        var reader = new ScriptedPasswordReader(NewPassword, NewPassword) { Output = promptOutput };

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "central",
            "mehmet@example.com",
            tenantSelector: null,
            dryRun: false,
            openTenant: (_, _) => throw new InvalidOperationException("Tenant database must not be opened."),
            reader,
            TestContext.Current.CancellationToken), promptOutput);

        var updated = await ReloadCentralAsync(central, user.Id);
        var updatedMembership = await central.TenantMemberships.AsNoTracking()
            .SingleAsync(x => x.Id == membership.Id, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Contains("Scope: Central", reader.OutputAtFirstPrompt);
        Assert.Contains("User: m***@example.com", reader.OutputAtFirstPrompt);
        Assert.Contains("Password updated successfully.", output);
        Assert.DoesNotContain(NewPassword, output);
        Assert.DoesNotContain(updated.PasswordHash, output);
        Assert.False(BCrypt.Net.BCrypt.Verify(OldPassword, updated.PasswordHash));
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, updated.PasswordHash));
        Assert.Equal(user.Id, updated.Id);
        Assert.Equal("mehmet@example.com", updated.Email);
        Assert.Equal("MEHMET@EXAMPLE.COM", updated.NormalizedEmail);
        Assert.Equal("Mehmet Admin", updated.DisplayName);
        Assert.True(updated.IsActive);
        Assert.Equal(createdAt, updated.CreatedAt);
        Assert.Equal(lastLogin, updated.LastLoginAt);
        Assert.True(updated.UpdatedAt > createdAt);
        Assert.Equal(otherHash, (await ReloadCentralAsync(central, other.Id)).PasswordHash);
        Assert.Equal("starter", updatedMembership.PlanCode);
        Assert.Equal(MembershipStatus.Active, updatedMembership.Status);
        Assert.Equal("mehmet@example.com", updatedMembership.OwnerEmail);
    }

    [Fact]
    public async Task CentralMismatchedConfirmation_MakesNoChange()
    {
        await using var central = await CreateCentralAsync();
        var user = await SeedCentralUserAsync(central, "mehmet@example.com", "Mehmet Admin");
        var originalHash = user.PasswordHash;
        var updatedAt = user.UpdatedAt;

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "central",
            "mehmet@example.com",
            tenantSelector: null,
            dryRun: false,
            openTenant: (_, _) => throw new InvalidOperationException("Tenant database must not be opened."),
            new ScriptedPasswordReader(NewPassword, "Different123!"),
            TestContext.Current.CancellationToken));

        var stored = await ReloadCentralAsync(central, user.Id);
        Assert.Equal(2, code);
        Assert.Contains("New password and confirmation do not match. No changes were made.", output);
        Assert.DoesNotContain(NewPassword, output);
        Assert.DoesNotContain("Different123!", output);
        Assert.Equal(originalHash, stored.PasswordHash);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, stored.PasswordHash));
    }

    [Fact]
    public async Task CentralInvalidPassword_MakesNoChange()
    {
        await using var central = await CreateCentralAsync();
        var user = await SeedCentralUserAsync(central, "mehmet@example.com", "Mehmet Admin");
        var originalHash = user.PasswordHash;

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "central",
            "mehmet@example.com",
            tenantSelector: null,
            dryRun: false,
            openTenant: (_, _) => throw new InvalidOperationException("Tenant database must not be opened."),
            new ScriptedPasswordReader("short", "short"),
            TestContext.Current.CancellationToken));

        var stored = await ReloadCentralAsync(central, user.Id);
        Assert.Equal(2, code);
        Assert.Contains("Password must be at least 8 characters.", output);
        Assert.Contains("No changes were made.", output);
        Assert.DoesNotContain("short", output);
        Assert.Equal(originalHash, stored.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, stored.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("short", stored.PasswordHash));
    }

    [Fact]
    public async Task InvalidScope_MissingEmail_AndMissingTenant_AreRejected()
    {
        await using var central = await CreateCentralAsync();

        var invalidScope = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central, "everywhere", "user@example.com", null, false, UnusedTenant, new ThrowingPasswordReader(), TestContext.Current.CancellationToken));
        var missingEmail = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central, "central", "  ", null, false, UnusedTenant, new ThrowingPasswordReader(), TestContext.Current.CancellationToken));
        var missingTenant = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central, "tenant", "user@example.com", "  ", false, UnusedTenant, new ThrowingPasswordReader(), TestContext.Current.CancellationToken));
        var centralWithTenant = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central, "central", "user@example.com", "mengen", false, UnusedTenant, new ThrowingPasswordReader(), TestContext.Current.CancellationToken));

        Assert.Equal(2, invalidScope.Code);
        Assert.Contains("Invalid scope.", invalidScope.Output);
        Assert.Equal(2, missingEmail.Code);
        Assert.Contains("Email is required.", missingEmail.Output);
        Assert.Equal(2, missingTenant.Code);
        Assert.Contains("Tenant scope requires --tenant <slug-or-id>.", missingTenant.Output);
        Assert.Equal(2, centralWithTenant.Code);
        Assert.Contains("Central scope does not use --tenant.", centralWithTenant.Output);
        Assert.Empty(await central.CentralAdminUsers.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TenantUser_IsResetOnlyInSpecifiedTenant()
    {
        await using var central = await CreateCentralAsync();
        var mengen = SeedTenant(central, "Mengen Lokantası", "mengen");
        var otherTenant = SeedTenant(central, "Diğer Lokanta", "diger");
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var mengenConnection = await CreateTenantConnectionAsync();
        await using var otherConnection = await CreateTenantConnectionAsync();
        var branchId = Guid.NewGuid();
        var mengenUserId = await SeedTenantUserAsync(mengenConnection, "mehmet@example.com", "Mehmet Usta", UserRole.Manager, branchId);
        var otherUserId = await SeedTenantUserAsync(otherConnection, "mehmet@example.com", "Mehmet Usta", UserRole.Owner, null);
        var onlyOtherId = await SeedTenantUserAsync(otherConnection, "only-other@example.com", "Başka Kullanıcı", UserRole.Viewer, null);
        var createdAt = (await ReadTenantUserAsync(mengenConnection, mengenUserId)).CreatedAt;
        var stampBefore = (await ReadTenantUserAsync(mengenConnection, mengenUserId)).SecurityStamp;
        var otherStampBefore = (await ReadTenantUserAsync(otherConnection, otherUserId)).SecurityStamp;
        var opened = new List<Guid>();

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "tenant",
            "mehmet@example.com",
            "mengen",
            dryRun: false,
            openTenant: (tenant, _) =>
            {
                opened.Add(tenant.Id);
                var connection = tenant.Id == mengen.Id ? mengenConnection : otherConnection;
                return Task.FromResult(CreateTenant(connection));
            },
            new ScriptedPasswordReader(NewPassword, NewPassword),
            TestContext.Current.CancellationToken));

        var updated = await ReadTenantUserAsync(mengenConnection, mengenUserId);
        var other = await ReadTenantUserAsync(otherConnection, otherUserId);
        var untouched = await ReadTenantUserAsync(otherConnection, onlyOtherId);
        Assert.Equal(0, code);
        Assert.Equal(new[] { mengen.Id }, opened);
        Assert.Contains("Scope: Tenant", output);
        Assert.Contains("Tenant: Mengen Lokantası", output);
        Assert.Contains("User: m***@example.com", output);
        Assert.Contains("Password updated successfully.", output);
        Assert.DoesNotContain(NewPassword, output);
        Assert.DoesNotContain(updated.PasswordHash, output);
        Assert.DoesNotContain("Mehmet Usta", output);
        Assert.False(BCrypt.Net.BCrypt.Verify(OldPassword, updated.PasswordHash));
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, updated.PasswordHash));
        Assert.Equal("mehmet@example.com", updated.Email);
        Assert.Equal("Mehmet Usta", updated.FullName);
        Assert.Equal(UserRole.Manager, updated.Role);
        Assert.Equal(branchId, updated.BranchId);
        Assert.True(updated.IsActive);
        Assert.Equal(createdAt, updated.CreatedAt);
        // A new stamp ends that user's existing Web sessions; the same email in another tenant keeps its own (WAS-89).
        Assert.NotEqual(stampBefore, updated.SecurityStamp);
        Assert.NotEqual(Guid.Empty, updated.SecurityStamp);
        Assert.Equal(otherStampBefore, other.SecurityStamp);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, other.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify(NewPassword, other.PasswordHash));
        Assert.Equal(UserRole.Owner, other.Role);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, untouched.PasswordHash));
        Assert.Equal(UserRole.Viewer, untouched.Role);
    }

    [Fact]
    public async Task UnknownTenant_AndUnknownTenantUser_AreRejected()
    {
        await using var central = await CreateCentralAsync();
        var mengen = SeedTenant(central, "Mengen Lokantası", "mengen");
        var otherTenant = SeedTenant(central, "Diğer Lokanta", "diger");
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var mengenConnection = await CreateTenantConnectionAsync();
        await using var otherConnection = await CreateTenantConnectionAsync();
        var otherUserId = await SeedTenantUserAsync(otherConnection, "mehmet@example.com", "Mehmet Usta", UserRole.Owner, null);
        var opened = new List<Guid>();

        var unknownTenant = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "tenant",
            "mehmet@example.com",
            "missing",
            dryRun: false,
            openTenant: (tenant, _) =>
            {
                opened.Add(tenant.Id);
                return Task.FromResult(CreateTenant(tenant.Id == mengen.Id ? mengenConnection : otherConnection));
            },
            new ScriptedPasswordReader(NewPassword, NewPassword),
            TestContext.Current.CancellationToken));

        var unknownUser = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "tenant",
            "mehmet@example.com",
            mengen.Id.ToString(),
            dryRun: false,
            openTenant: (tenant, _) =>
            {
                opened.Add(tenant.Id);
                return Task.FromResult(CreateTenant(tenant.Id == mengen.Id ? mengenConnection : otherConnection));
            },
            new ScriptedPasswordReader(NewPassword, NewPassword),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, unknownTenant.Code);
        Assert.Contains("Unknown tenant. No changes were made.", unknownTenant.Output);
        Assert.Equal(2, unknownUser.Code);
        Assert.Contains("No matching user was found. No changes were made.", unknownUser.Output);
        Assert.Equal(new[] { mengen.Id }, opened);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await ReadTenantUserAsync(otherConnection, otherUserId)).PasswordHash));
        Assert.NotEqual(otherTenant.Id, mengen.Id);
    }

    [Fact]
    public async Task TenantAmbiguousUser_IsRejected()
    {
        await using var central = await CreateCentralAsync();
        var mengen = SeedTenant(central, "Mengen Lokantası", "mengen");
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var connection = await CreateTenantConnectionAsync();
        await using (var db = CreateTenant(connection))
            await db.Database.ExecuteSqlRawAsync(
                "DROP INDEX IF EXISTS \"IX_AppUsers_Email\"",
                TestContext.Current.CancellationToken);
        var firstId = await SeedTenantUserAsync(connection, "mehmet@example.com", "First", UserRole.Manager, null);
        var secondId = await SeedTenantUserAsync(connection, "mehmet@example.com", "Second", UserRole.Cashier, null);
        var firstHash = (await ReadTenantUserAsync(connection, firstId)).PasswordHash;
        var secondHash = (await ReadTenantUserAsync(connection, secondId)).PasswordHash;

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "tenant",
            "mehmet@example.com",
            "mengen",
            dryRun: false,
            openTenant: (_, _) => Task.FromResult(CreateTenant(connection)),
            new ScriptedPasswordReader(NewPassword, NewPassword),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, code);
        Assert.Contains("More than one matching user was found. No changes were made.", output);
        Assert.Equal(firstHash, (await ReadTenantUserAsync(connection, firstId)).PasswordHash);
        Assert.Equal(secondHash, (await ReadTenantUserAsync(connection, secondId)).PasswordHash);
        Assert.Equal(UserRole.Manager, (await ReadTenantUserAsync(connection, firstId)).Role);
        Assert.Equal(UserRole.Cashier, (await ReadTenantUserAsync(connection, secondId)).Role);
        Assert.NotEqual(Guid.Empty, mengen.Id);
    }

    [Fact]
    public async Task TenantDryRun_ByTenantId_DoesNotPromptOrWrite()
    {
        await using var central = await CreateCentralAsync();
        var mengen = SeedTenant(central, "Mengen Lokantası", "mengen");
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var connection = await CreateTenantConnectionAsync();
        var userId = await SeedTenantUserAsync(connection, "mehmet@example.com", "Mehmet Usta", UserRole.Kitchen, null);
        var originalHash = (await ReadTenantUserAsync(connection, userId)).PasswordHash;
        var now = DateTime.UtcNow;
        Guid activeTokenId;
        await using (var db = CreateTenant(connection))
        {
            var active = new PasswordResetToken
            {
                UserId = userId,
                TokenHash = "active-token-hash",
                ExpiresAtUtc = now.AddMinutes(30),
                CreatedAtUtc = now
            };
            db.PasswordResetTokens.Add(active);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            activeTokenId = active.Id;
        }

        var (code, output) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "tenant",
            "mehmet@example.com",
            mengen.Id.ToString(),
            dryRun: true,
            openTenant: (_, _) => Task.FromResult(CreateTenant(connection)),
            new ThrowingPasswordReader(),
            TestContext.Current.CancellationToken));

        await using var verify = CreateTenant(connection);
        var token = await verify.PasswordResetTokens.SingleAsync(
            t => t.Id == activeTokenId,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Contains("Dry run: password would be reset. No changes were written.", output);
        Assert.Contains("Scope: Tenant", output);
        Assert.Contains("Tenant: Mengen Lokantası", output);
        Assert.Contains("User: m***@example.com", output);
        Assert.DoesNotContain("Password updated successfully.", output);
        Assert.Equal(originalHash, (await ReadTenantUserAsync(connection, userId)).PasswordHash);
        Assert.Null(token.UsedAtUtc);
    }

    [Fact]
    public async Task TenantPasswordChange_InvalidatesActiveResetTokens_Only()
    {
        await using var central = await CreateCentralAsync();
        SeedTenant(central, "Mengen Lokantası", "mengen");
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using var connection = await CreateTenantConnectionAsync();
        var userId = await SeedTenantUserAsync(connection, "mehmet@example.com", "Mehmet Usta", UserRole.Owner, null);
        var otherUserId = await SeedTenantUserAsync(connection, "other@example.com", "Other", UserRole.Viewer, null);
        var now = DateTime.UtcNow;
        Guid activeId, expiredId, otherId;
        await using (var db = CreateTenant(connection))
        {
            var active = new PasswordResetToken { UserId = userId, TokenHash = "active", ExpiresAtUtc = now.AddHours(1), CreatedAtUtc = now };
            var expired = new PasswordResetToken { UserId = userId, TokenHash = "expired", ExpiresAtUtc = now.AddMinutes(-5), CreatedAtUtc = now };
            var other = new PasswordResetToken { UserId = otherUserId, TokenHash = "other", ExpiresAtUtc = now.AddHours(1), CreatedAtUtc = now };
            db.PasswordResetTokens.AddRange(active, expired, other);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            activeId = active.Id;
            expiredId = expired.Id;
            otherId = other.Id;
        }

        var (code, _) = await CaptureAsync(() => CliPasswordReset.ExecuteAsync(
            central,
            "TENANT",
            "mehmet@example.com",
            "mengen",
            dryRun: false,
            openTenant: (_, _) => Task.FromResult(CreateTenant(connection)),
            new ScriptedPasswordReader(NewPassword, NewPassword),
            TestContext.Current.CancellationToken));

        await using var verify = CreateTenant(connection);
        var tokens = await verify.PasswordResetTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.NotNull(tokens.Single(t => t.Id == activeId).UsedAtUtc);
        Assert.Null(tokens.Single(t => t.Id == expiredId).UsedAtUtc);
        Assert.Null(tokens.Single(t => t.Id == otherId).UsedAtUtc);
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, (await ReadTenantUserAsync(connection, userId)).PasswordHash));
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await ReadTenantUserAsync(connection, otherUserId)).PasswordHash));
    }

    private static async Task<CentralDbContext> CreateCentralAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var db = CreateCentral(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    private static CentralDbContext CreateCentral(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(connection).Options);

    private static async Task<SqliteConnection> CreateTenantConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
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

            CREATE TABLE PasswordResetTokens (
                Id TEXT NOT NULL CONSTRAINT PK_PasswordResetTokens PRIMARY KEY,
                UserId TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                UsedAtUtc TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_PasswordResetTokens_AppUsers_UserId FOREIGN KEY (UserId) REFERENCES AppUsers (Id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static TenantDbContext CreateTenant(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options);

    private static Tenant SeedTenant(CentralDbContext central, string name, string slug)
    {
        var tenant = new Tenant
        {
            Name = name,
            Slug = slug,
            PrimaryDomain = slug + ".wasla.local",
            DatabaseName = "wasla_" + slug,
            EncryptedConnectionString = "not-used-in-test",
            EncryptionKeyVersion = 1,
            IsActive = true
        };
        central.Tenants.Add(tenant);
        return tenant;
    }

    private static async Task<CentralAdminUser> SeedCentralUserAsync(CentralDbContext central, string email, string displayName)
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var user = new CentralAdminUser
        {
            Email = email,
            NormalizedEmail = email.Trim().ToUpperInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(OldPassword),
            DisplayName = displayName,
            IsActive = true,
            LastLoginAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        central.CentralAdminUsers.Add(user);
        await central.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private static async Task<Guid> SeedTenantUserAsync(
        SqliteConnection connection,
        string email,
        string fullName,
        UserRole role,
        Guid? branchId)
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await using var db = CreateTenant(connection);
        var user = new AppUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(OldPassword),
            FullName = fullName,
            Role = role,
            BranchId = branchId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static async Task<CentralAdminUser> ReloadCentralAsync(CentralDbContext central, Guid id)
    {
        var local = central.CentralAdminUsers.Local.FirstOrDefault(x => x.Id == id);
        if (local is not null)
            await central.Entry(local).ReloadAsync(TestContext.Current.CancellationToken);
        return await central.CentralAdminUsers.AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task<AppUser> ReadTenantUserAsync(SqliteConnection connection, Guid id)
    {
        await using var db = CreateTenant(connection);
        return await db.AppUsers.AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    private static Task<TenantDbContext> UnusedTenant(Tenant tenant, CancellationToken ct) =>
        throw new InvalidOperationException("Tenant database must not be opened.");

    private static async Task<(int Code, string Output)> CaptureAsync(Func<Task<int>> action, StringWriter? writer = null)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var owned = writer is null;
        writer ??= new StringWriter();
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
            if (owned)
                await writer.DisposeAsync();
        }
    }

    private sealed class ScriptedPasswordReader : ICliPasswordReader
    {
        private readonly string[] _values;
        private int _index;

        public ScriptedPasswordReader(params string[] values) => _values = values;

        public StringWriter? Output { get; init; }

        public int ReadCount { get; private set; }

        public string? OutputAtFirstPrompt { get; private set; }

        public CliSecretPrompt ReadSecret(string prompt)
        {
            if (ReadCount == 0)
                OutputAtFirstPrompt = Output?.ToString();
            ReadCount++;
            return new CliSecretPrompt(true, _values[_index++]);
        }
    }

    private sealed class ThrowingPasswordReader : ICliPasswordReader
    {
        public CliSecretPrompt ReadSecret(string prompt) =>
            throw new InvalidOperationException("Password prompt must not run.");
    }
}
