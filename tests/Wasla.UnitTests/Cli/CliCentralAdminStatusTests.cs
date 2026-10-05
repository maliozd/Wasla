using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-37. <c>disable-central-admin</c> / <c>enable-central-admin</c>: the supported way to change a central admin's
/// status. Each change rotates the security stamp in the same UPDATE; repeats and dry runs write nothing.
/// The cookie-level effect is in <see cref="CentralAdminSessionRevalidationTests"/>.
/// </summary>
public sealed class CliCentralAdminStatusTests : IDisposable
{
    private const string Email = "mehmet@example.com";
    private const string OtherEmail = "other@example.com";
    private const string PasswordHash = "ADMIN-PWHASH-SECRET-status";

    private static readonly DateTime Seeded = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly CentralTestDatabase _central = new();

    public CliCentralAdminStatusTests()
    {
        Seed(Email, "Mehmet Admin", isActive: true);
        Seed(OtherEmail, "Other Admin", isActive: true);
    }

    public void Dispose() => _central.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Disable and enable --------------------------------------------------------------------------------

    [Fact]
    public async Task Disable_DisablesTheAccount_AndRotatesItsStampInTheSameUpdate()
    {
        var before = Read(Email);
        var other = Read(OtherEmail);

        var (code, output, commands) = await RunCountedAsync(Email, enable: false);

        var after = Read(Email);
        Assert.Equal(0, code);
        Assert.False(after.IsActive);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.NotEqual(Guid.Empty, after.SecurityStamp);
        Assert.True(after.UpdatedAt > Seeded);
        Assert.Contains("Account disabled. All of its existing sessions are signed out.", output);

        // One statement changes the status and the stamp together.
        var update = Assert.Single(commands, IsWrite);
        Assert.StartsWith("UPDATE \"CentralAdminUsers\"", update.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("\"IsActive\"", update, StringComparison.Ordinal);
        Assert.Contains("\"SecurityStamp\"", update, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PasswordHash\"", update, StringComparison.Ordinal);

        // No one else is affected.
        var otherAfter = Read(OtherEmail);
        Assert.True(otherAfter.IsActive);
        Assert.Equal(other.SecurityStamp, otherAfter.SecurityStamp);
    }

    [Fact]
    public async Task Enable_EnablesTheAccount_WithANewStampAgain()
    {
        await RunAsync(Email, enable: false);
        var disabled = Read(Email);

        var (code, output, commands) = await RunCountedAsync(Email, enable: true);

        var enabled = Read(Email);
        Assert.Equal(0, code);
        Assert.True(enabled.IsActive);
        Assert.NotEqual(disabled.SecurityStamp, enabled.SecurityStamp);
        Assert.Contains("Account enabled. Sessions issued before it was disabled stay signed out.", output);
        var update = Assert.Single(commands, IsWrite);
        Assert.Contains("\"SecurityStamp\"", update, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_AfterADisableByDirectSqlWithoutAStampChange_StillRevokesTheOldStamp()
    {
        var original = Read(Email).SecurityStamp;
        using (var db = _central.CreateContext())
            db.Database.ExecuteSqlRaw("UPDATE \"CentralAdminUsers\" SET \"IsActive\" = 0 WHERE \"NormalizedEmail\" = {0}", Email.ToUpperInvariant());
        Assert.Equal(original, Read(Email).SecurityStamp);

        var (code, _, _) = await RunCountedAsync(Email, enable: true);

        Assert.Equal(0, code);
        Assert.True(Read(Email).IsActive);
        Assert.NotEqual(original, Read(Email).SecurityStamp);
    }

    // Repeats and dry runs ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "The account is already disabled. No changes were made.")]
    [InlineData(true, "The account is already enabled. No changes were made.")]
    public async Task RepeatingACommand_ChangesNothing(bool enable, string message)
    {
        if (!enable)
            await RunAsync(Email, enable: false);
        var before = Read(Email);

        var (code, output, commands) = await RunCountedAsync(Email, enable);

        var after = Read(Email);
        Assert.Equal(0, code);
        Assert.Contains(message, output);
        Assert.DoesNotContain(commands, IsWrite);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Theory]
    [InlineData(false, "The account would be disabled and all of its existing sessions signed out.")]
    [InlineData(true, "The account would be enabled. Sessions issued before it was disabled would stay signed out.")]
    public async Task DryRun_ResolvesTheAccount_AndWritesNothing(bool enable, string message)
    {
        if (enable)
            await RunAsync(Email, enable: false);
        var before = Read(Email);

        var (code, output, commands) = await RunCountedAsync(Email, enable, dryRun: true);

        var after = Read(Email);
        Assert.Equal(0, code);
        Assert.Contains("Dry run: no changes will be written.", output);
        Assert.Contains(message, output);
        Assert.Contains("User: m***@example.com", output);
        Assert.DoesNotContain(commands, IsWrite);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // Target resolution -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAccountIsResolvedByTrimmedCaseInsensitiveEmail()
    {
        var (code, _, _) = await RunCountedAsync("  MEHMET@Example.COM ", enable: false);

        Assert.Equal(0, code);
        Assert.False(Read(Email).IsActive);
    }

    [Theory]
    [InlineData("missing@example.com", "No matching user was found. No changes were made.")]
    [InlineData("   ", "Email is required.")]
    [InlineData(null, "Email is required.")]
    public async Task UnknownOrMissingEmail_IsRejected(string? email, string message)
    {
        var (code, output, commands) = await RunCountedAsync(email, enable: false);

        Assert.Equal(2, code);
        Assert.Contains(message, output);
        Assert.DoesNotContain(commands, IsWrite);
        Assert.True(Read(Email).IsActive);
    }

    [Fact]
    public async Task AmbiguousEmail_IsRejected_WithoutChangingEitherAccount()
    {
        using (var db = _central.CreateContext())
            db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS \"IX_CentralAdminUsers_NormalizedEmail\"");
        Seed("Mehmet@Example.com", "Duplicate", isActive: true);
        var stamps = AllStamps();

        var (code, output, commands) = await RunCountedAsync(Email, enable: false);

        Assert.Equal(2, code);
        Assert.Contains("More than one matching user was found. No changes were made.", output);
        Assert.DoesNotContain(commands, IsWrite);
        Assert.Equal(stamps, AllStamps());
    }

    // Output and failures ----------------------------------------------------------------------------------

    [Fact]
    public async Task Output_NamesTheAccountByMaskedEmailOnly()
    {
        var before = Read(Email);

        var (_, disableOutput, _) = await RunCountedAsync(Email, enable: false);
        var afterDisable = Read(Email);
        var (_, enableOutput, _) = await RunCountedAsync(Email, enable: true);
        var afterEnable = Read(Email);

        foreach (var output in new[] { disableOutput, enableOutput })
        {
            Assert.Contains("Scope: Central", output);
            Assert.Contains("User: m***@example.com", output);
            Assert.DoesNotContain(Email, output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mehmet Admin", output);
            Assert.DoesNotContain(PasswordHash, output);
            foreach (var stamp in new[] { before.SecurityStamp, afterDisable.SecurityStamp, afterEnable.SecurityStamp })
            {
                Assert.DoesNotContain(stamp.ToString(), output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(stamp.ToString("N"), output, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task DatabaseFailure_Exits3_WithoutSqlDetail()
    {
        using (var db = _central.CreateContext())
            db.Database.ExecuteSqlRaw("ALTER TABLE \"CentralAdminUsers\" RENAME TO \"CentralAdminUsers_Unavailable\"");

        var (code, output, _) = await RunCountedAsync(Email, enable: false);

        Assert.Equal(3, code);
        Assert.Contains("Database operation failed. Check the account with list-central-admins before retrying.", output);
        Assert.DoesNotContain("no such table", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CentralAdminUsers", output, StringComparison.Ordinal);
    }

    // Command wiring ----------------------------------------------------------------------------------------

    [Fact]
    public async Task HostedCommand_UsesTheRegisteredCentralDbContext()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddDbContext<CentralDbContext>(options => options.UseSqlite(_central.ConnectionString)))
            .Build();
        var before = Read(Email).SecurityStamp;
        using var output = new StringWriter();

        var code = await CliCommands.SetCentralAdminEnabledAsync(host, Email, enable: false, dryRun: false, Ct, output);

        Assert.Equal(0, code);
        Assert.False(Read(Email).IsActive);
        Assert.NotEqual(before, Read(Email).SecurityStamp);
    }

    [Theory]
    [InlineData("disable-central-admin")]
    [InlineData("enable-central-admin")]
    public void Commands_AreKnownToTheCli(string command) => Assert.True(CliHelpPrinter.IsKnownCommand(command));

    // Helpers -----------------------------------------------------------------------------------------------

    private static bool IsWrite(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunAsync(string email, bool enable)
    {
        await using var db = _central.CreateContext();
        return await CliCentralAdminStatus.ExecuteAsync(db, email, enable, dryRun: false, TextWriter.Null, Ct);
    }

    private async Task<(int Code, string Output, IReadOnlyList<string> Commands)> RunCountedAsync(string? email, bool enable, bool dryRun = false)
    {
        await using var db = _central.CreateContext(counted: true);
        _central.Counter.Reset();
        using var output = new StringWriter();
        var code = await CliCentralAdminStatus.ExecuteAsync(db, email, enable, dryRun, output, Ct);
        return (code, output.ToString(), _central.Counter.Commands);
    }

    private CentralAdminUser Read(string email)
    {
        using var db = _central.CreateContext();
        return db.CentralAdminUsers.AsNoTracking().Single(x => x.NormalizedEmail == email.ToUpperInvariant());
    }

    private List<Guid> AllStamps()
    {
        using var db = _central.CreateContext();
        return db.CentralAdminUsers.AsNoTracking().OrderBy(x => x.Id).Select(x => x.SecurityStamp).ToList();
    }

    private void Seed(string email, string displayName, bool isActive)
    {
        using var db = _central.CreateContext();
        db.CentralAdminUsers.Add(new CentralAdminUser
        {
            Email = email,
            NormalizedEmail = email.Trim().ToUpperInvariant(),
            DisplayName = displayName,
            PasswordHash = PasswordHash,
            IsActive = isActive,
            CreatedAt = Seeded,
            UpdatedAt = Seeded
        });
        db.SaveChanges();
    }
}
