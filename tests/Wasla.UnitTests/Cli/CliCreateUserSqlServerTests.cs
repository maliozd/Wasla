using System.CommandLine;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Security;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.UnitTests.Orders;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-42: <c>create-user</c> through the registered command tree and the CLI's own tenant-database path (the stored
/// connection string decrypted by the secret manager, then SQL Server), against a throwaway tenant database migrated
/// with this build's tenant migrations (<see cref="SqlServerTenantDatabase"/>). CentralDb is in-memory SQLite with one
/// fake tenant, and the secret manager is a fake that knows only that tenant's connection string. Shows what SQLite
/// cannot: SQL Server's case-insensitive email comparison refuses a second user whose email differs only in case.
/// </summary>
[Collection(nameof(CliConsoleCollection))]
public sealed class CliCreateUserSqlServerTests(SqlServerTenantDatabase database) : IClassFixture<SqlServerTenantDatabase>
{
    private const string FakePassword = "Fake-password-1";
    private const string EncryptedConnectionString = "fake-encrypted-tenant-connection";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateUser_StoresTheUserInTheTenantDatabase_AndRefusesTheSameEmailInAnotherCase()
    {
        await using var central = new SqliteConnection("Data Source=:memory:");
        await central.OpenAsync(Ct);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(central));
                services.AddSingleton<ISecretManager>(new OneTenantSecretManager(database.ConnectionString("Cli")));
            })
            .Build();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            await db.Database.EnsureCreatedAsync(Ct);
            db.Tenants.Add(new Tenant
            {
                Name = "Fake Demo",
                Slug = "demo",
                PrimaryDomain = "demo.app.wasla.local",
                DatabaseName = database.DatabaseName,
                EncryptedConnectionString = EncryptedConnectionString,
                EncryptionKeyVersion = 1,
                IsActive = true
            });
            await db.SaveChangesAsync(Ct);
        }

        var created = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(
            ["create-user", "--tenant-slug", "demo", "--email", "owner@example.com", "--password", FakePassword, "--role", "Owner", "--name", "Fake Owner"]));

        Assert.Equal(0, created.Code);
        Assert.Contains("✓ User owner@example.com created (role: Owner).", created.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, created.Output, StringComparison.Ordinal);

        var duplicate = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(
            ["create-user", "--tenant-slug", "demo", "--email", "OWNER@Example.com", "--password", "Other-fake-password-2", "--role", "Kitchen"]));

        Assert.Equal(2, duplicate.Code);
        Assert.Contains("A user with this email already exists.", duplicate.Output, StringComparison.Ordinal);

        await using var tenant = database.Open();
        var user = Assert.Single(await tenant.AppUsers.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("owner@example.com", user.Email);
        Assert.Equal("Fake Owner", user.FullName);
        Assert.Equal(UserRole.Owner, user.Role);
        Assert.True(user.IsActive);
        Assert.True(BCrypt.Net.BCrypt.Verify(FakePassword, user.PasswordHash));
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

    private sealed class OneTenantSecretManager(string connectionString) : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            throw new InvalidOperationException("No secret may be encrypted by this test.");

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            encryptedBase64 == EncryptedConnectionString && keyVersion == 1
                ? Task.FromResult(connectionString)
                : throw new InvalidOperationException("Only the fake tenant's connection string can be decrypted.");
    }
}
