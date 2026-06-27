using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

public sealed class TenantRolesAuthorizationTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly TenantDbHarness _db = new();

    [Theory]
    [InlineData(UserRole.Owner, TenantPolicies.CanManageDeviceSecurity, true)]
    [InlineData(UserRole.Viewer, TenantPolicies.CanManagePrintBridgeDevices, false)]
    [InlineData(UserRole.Kitchen, TenantPolicies.CanManageDeviceSecurity, false)]
    [InlineData(UserRole.Cashier, TenantPolicies.CanManualPrint, true)]
    [InlineData(UserRole.Cashier, TenantPolicies.CanManageDeviceSecurity, false)]
    [InlineData(UserRole.Manager, TenantPolicies.CanManageOrders, true)]
    public async Task TenantRolePolicies_ApplyExpectedRoleMapping(
        UserRole role,
        string policyName,
        bool expected)
    {
        var service = BuildAuthorizationService(_tenantId);
        var result = await service.AuthorizeAsync(Principal(_tenantId, role), null, policyName);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task TenantRolePolicy_DoesNotAuthorizeRoleFromAnotherTenant()
    {
        var service = BuildAuthorizationService(_tenantId);

        var result = await service.AuthorizeAsync(
            Principal(_otherTenantId, UserRole.Owner),
            null,
            TenantPolicies.CanManageDeviceSecurity);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("OwNeR")]
    public async Task TenantRolePolicy_AcceptsRoleClaimCasingVariants(string roleClaim)
    {
        var service = BuildAuthorizationService(_tenantId);

        var result = await service.AuthorizeAsync(
            Principal(_tenantId, roleClaim),
            null,
            TenantPolicies.CanManageDeviceSecurity);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task TenantRolePolicy_RejectsInvalidRoleClaim()
    {
        var service = BuildAuthorizationService(_tenantId);

        var result = await service.AuthorizeAsync(
            Principal(_tenantId, "Administrator"),
            null,
            TenantPolicies.CanManageDeviceSecurity);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CentralAdminPrincipal_DoesNotSatisfyTenantRolePolicy()
    {
        var service = BuildAuthorizationService(_tenantId);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "CentralAdmin")],
            authenticationType: "CentralAdmin"));

        var result = await service.AuthorizeAsync(principal, null, TenantPolicies.CanManageTenantUsers);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthValidation_ReturnsTenantRoleForActiveUser()
    {
        var user = await SeedUserAsync(_tenantId, UserRole.Kitchen);
        var service = new AuthValidationService(_db);

        var result = await service.ValidateAsync(
            _tenantId,
            user.Email,
            "password",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(UserRole.Kitchen, result!.Role);
        Assert.Equal(_tenantId, result.CustomerId);
    }

    [Fact]
    public async Task AuthValidation_BlocksInactiveUser()
    {
        var user = await SeedUserAsync(_tenantId, UserRole.Owner, isActive: false);
        var service = new AuthValidationService(_db);

        var result = await service.ValidateAsync(
            _tenantId,
            user.Email,
            "password",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TenantUserRoleService_DoesNotDemoteLastOwner()
    {
        var owner = await SeedUserAsync(_tenantId, UserRole.Owner);
        var service = new TenantUserRoleService(_db);

        var result = await service.ChangeRoleAsync(
            _tenantId,
            owner.Id,
            UserRole.Manager,
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved, result.Outcome);
    }

    [Fact]
    public async Task TenantUserRoleService_DoesNotDisableLastOwner()
    {
        var owner = await SeedUserAsync(_tenantId, UserRole.Owner);
        var service = new TenantUserRoleService(_db);

        var result = await service.SetActiveAsync(
            _tenantId,
            owner.Id,
            isActive: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved, result.Outcome);
    }

    [Fact]
    public async Task TenantUserRoleService_DoesNotRemoveLastOwner()
    {
        var owner = await SeedUserAsync(_tenantId, UserRole.Owner);
        var service = new TenantUserRoleService(_db);

        var result = await service.RemoveAsync(
            _tenantId,
            owner.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantUserRoleUpdateOutcome.LastOwnerWouldBeRemoved, result.Outcome);
    }

    [Fact]
    public async Task TenantUserRoleService_AllowsManagerChangeWhenAnotherOwnerExists()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner, email: "owner2@example.test");
        var owner = await SeedUserAsync(_tenantId, UserRole.Owner);
        var service = new TenantUserRoleService(_db);

        var result = await service.ChangeRoleAsync(
            _tenantId,
            owner.Id,
            UserRole.Manager,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    private async Task<AppUser> SeedUserAsync(
        Guid tenantId,
        UserRole role,
        bool isActive = true,
        string email = "owner@example.test")
    {
        await using var db = await _db.CreateAsync(tenantId, TestContext.Current.CancellationToken);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password"),
            FullName = "Test User",
            Role = role,
            IsActive = isActive
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private static IAuthorizationService BuildAuthorizationService(Guid currentTenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            AddPolicy(options, TenantPolicies.CanManageTenantUsers, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
        });
        services.AddSingleton<ICurrentTenantService>(new FixedCurrentTenantService(currentTenantId));
        services.AddScoped<IAuthorizationHandler, TenantRoleAuthorizationHandler>();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static void AddPolicy(AuthorizationOptions options, string name, params UserRole[] roles)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new TenantRoleRequirement(roles));
        });
    }

    private static ClaimsPrincipal Principal(Guid tenantId, UserRole role) =>
        Principal(tenantId, role.ToString());

    private static ClaimsPrincipal Principal(Guid tenantId, string role) =>
        new(new ClaimsIdentity(
            [
                new Claim("TenantId", tenantId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ],
            authenticationType: "Tenant"));

    public void Dispose() => _db.Dispose();

    private sealed class FixedCurrentTenantService : ICurrentTenantService
    {
        public FixedCurrentTenantService(Guid tenantId)
        {
            CurrentTenant = new ResolvedTenantDto(tenantId, "Tenant", "tenant", "tenant.wasla.local");
        }

        public ResolvedTenantDto? CurrentTenant { get; }
    }

    private sealed class TenantDbHarness : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public async Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync(ct);
                _connections.Add(customerId, connection);

                await CreateSchemaAsync(connection, ct);
            }

            var contextOptions = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(connection)
                .Options;
            return new TenantDbContext(contextOptions);
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AppUsers (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Email TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    FullName TEXT NOT NULL,
                    Role INTEGER NOT NULL,
                    BranchId TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IX_AppUsers_Email ON AppUsers (Email);
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
