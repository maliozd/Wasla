using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Plans;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Setup;

/// <summary>
/// The restaurant setup step reads the business phone and location from the tenant's membership row.
/// Paid signup always created it; CLI-provisioned tenants did not, which left their setup panel stuck.
/// </summary>
public sealed class TenantMembershipSetupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IHost _host;

    public TenantMembershipSetupTests()
    {
        _connection.Open();
        _host = new HostBuilder()
            .ConfigureServices(services =>
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(_connection)))
            .Build();

        using var scope = _host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CentralDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task CliProvisionedTenant_WithContact_CanCompleteTheRestaurantStep()
    {
        var tenantId = await SeedTenantAsync("cli-complete");
        await using (var central = NewCentral())
        {
            var created = await TenantMembershipRecords.EnsureAsync(
                central,
                tenantId,
                CliCommands.CliMembershipSeed(" owner@cli.example.test ", new TenantContactUpdate("+90 212 000 00 00", "Istanbul", null)),
                Now,
                CancellationToken.None);
            Assert.True(created);
        }

        var membership = await ReadMembershipAsync(tenantId);
        Assert.Equal(WaslaPlanCodes.Starter, membership.PlanCode);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Null(membership.TrialEndsAt);
        Assert.Equal("owner@cli.example.test", membership.OwnerEmail);
        Assert.True((await ReadStatusAsync(tenantId)).Restaurant.IsComplete);
    }

    [Fact]
    public async Task CliProvisionedTenant_WithoutContact_IsUnblockedByUpdateCustomerProfile_AndRepeatIsANoOp()
    {
        var tenantId = await SeedTenantAsync("cli-later");
        await using (var central = NewCentral())
        {
            await TenantMembershipRecords.EnsureAsync(
                central,
                tenantId,
                CliCommands.CliMembershipSeed("owner@cli.example.test", new TenantContactUpdate(null, " ", null)),
                Now,
                CancellationToken.None);
        }

        Assert.False((await ReadStatusAsync(tenantId)).Restaurant.IsComplete);

        var contact = new TenantContactUpdate("+90 212 000 00 00", "Ankara", null);
        Assert.Equal(0, await (CliCommands.UpdateCustomerProfileAsync(_host, "cli-later", contact, CancellationToken.None)));
        Assert.True((await ReadStatusAsync(tenantId)).Restaurant.IsComplete);
        var first = await ReadMembershipAsync(tenantId);

        Assert.Equal(0, await (CliCommands.UpdateCustomerProfileAsync(_host, tenantId.ToString(), contact, CancellationToken.None)));
        var second = await ReadMembershipAsync(tenantId);

        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
        Assert.Equal("owner@cli.example.test", second.OwnerEmail);
        Assert.Equal(1, await CountMembershipsAsync(tenantId));
    }

    [Fact]
    public async Task UpdateCustomerProfile_CreatesTheMissingMembership_ForAnExistingCliTenant()
    {
        var tenantId = await SeedTenantAsync("cli-old");

        var exit = await (CliCommands.UpdateCustomerProfileAsync(
            _host, "cli-old", new TenantContactUpdate("+90 312 000 00 00", null, "Türkiye"), CancellationToken.None));

        Assert.Equal(0, exit);
        var membership = await ReadMembershipAsync(tenantId);
        Assert.Equal("Türkiye", membership.Country);
        Assert.Null(membership.City);
        Assert.True((await ReadStatusAsync(tenantId)).Restaurant.IsComplete);
    }

    [Fact]
    public async Task UpdateCustomerProfile_KeepsOmittedFields_AndRejectsInvalidInput()
    {
        var tenantId = await SeedTenantAsync("cli-partial");
        await (CliCommands.UpdateCustomerProfileAsync(
            _host, "cli-partial", new TenantContactUpdate("+90 212 000 00 00", "Izmir", null), CancellationToken.None));

        await (CliCommands.UpdateCustomerProfileAsync(
            _host, "cli-partial", new TenantContactUpdate(null, null, "Türkiye"), CancellationToken.None));

        var membership = await ReadMembershipAsync(tenantId);
        Assert.Equal("+90 212 000 00 00", membership.BusinessPhone);
        Assert.Equal("Izmir", membership.City);
        Assert.Equal("Türkiye", membership.Country);

        Assert.Equal(2, await (CliCommands.UpdateCustomerProfileAsync(
            _host, "cli-partial", new TenantContactUpdate(null, null, null), CancellationToken.None)));
        Assert.Equal(2, await (CliCommands.UpdateCustomerProfileAsync(
            _host, "cli-partial", new TenantContactUpdate(new string('1', 51), null, null), CancellationToken.None)));
        Assert.Equal(2, await (CliCommands.UpdateCustomerProfileAsync(
            _host, "missing-tenant", new TenantContactUpdate("+90 212 000 00 00", null, null), CancellationToken.None)));
    }

    [Fact]
    public async Task Ensure_NeverOverwritesAPaidSignupMembership()
    {
        var tenantId = await SeedTenantAsync("paid");
        await using (var central = NewCentral())
        {
            Assert.True(await TenantMembershipRecords.EnsureAsync(
                central,
                tenantId,
                new TenantMembershipSeed("Pro", "Yearly", MembershipStatus.Trial, Now.AddDays(14),
                    "owner@paid.example.test", "+90 216 000 00 00", "Istanbul", "Türkiye", "restaurant"),
                Now,
                CancellationToken.None));
        }

        await using (var central = NewCentral())
        {
            Assert.False(await TenantMembershipRecords.EnsureAsync(
                central,
                tenantId,
                CliCommands.CliMembershipSeed("someone@else.example.test", new TenantContactUpdate("0", "Elsewhere", null)),
                Now.AddDays(1),
                CancellationToken.None));
        }

        var membership = await ReadMembershipAsync(tenantId);
        Assert.Equal("Pro", membership.PlanCode);
        Assert.Equal(MembershipStatus.Trial, membership.Status);
        Assert.Equal("owner@paid.example.test", membership.OwnerEmail);
        Assert.Equal("+90 216 000 00 00", membership.BusinessPhone);
        Assert.Equal(1, await CountMembershipsAsync(tenantId));
    }

    public void Dispose()
    {
        _host.Dispose();
        _connection.Dispose();
    }

    private CentralDbContext NewCentral() =>
        new(new DbContextOptionsBuilder<CentralDbContext>().UseSqlite(_connection).Options);

    private async Task<Guid> SeedTenantAsync(string slug)
    {
        await using var central = NewCentral();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Setup " + slug,
            Slug = slug,
            PrimaryDomain = slug + ".wasla.local",
            DatabaseName = "Wasla_" + slug.Replace("-", string.Empty),
            EncryptedConnectionString = "encrypted",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        central.Tenants.Add(tenant);
        await central.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<TenantMembership> ReadMembershipAsync(Guid tenantId)
    {
        await using var central = NewCentral();
        return await central.TenantMemberships.AsNoTracking().SingleAsync(m => m.TenantId == tenantId);
    }

    private async Task<int> CountMembershipsAsync(Guid tenantId)
    {
        await using var central = NewCentral();
        return await central.TenantMemberships.CountAsync(m => m.TenantId == tenantId);
    }

    private async Task<Wasla.Application.Abstractions.Setup.TenantSetupStatus> ReadStatusAsync(Guid tenantId)
    {
        await using var central = NewCentral();
        var service = new TenantSetupStatusService(
            central, new UnavailableTenantDatabases(), TimeProvider.System, NullLogger<TenantSetupStatusService>.Instance);
        return await service.GetAsync(tenantId, Guid.NewGuid(), CancellationToken.None);
    }

    // CLI output is left on the test console: redirecting the global Console here would race with
    // the CLI tests that capture and assert on it in parallel.

    /// <summary>The restaurant step reads only CentralDb; the tenant-database steps degrade on their own.</summary>
    private sealed class UnavailableTenantDatabases : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct) =>
            throw new InvalidOperationException("Tenant database is not part of this test.");
    }
}
