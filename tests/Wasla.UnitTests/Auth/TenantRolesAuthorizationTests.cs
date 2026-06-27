using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.Models.TenantUsers;
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
    public void TenantCookie_UsesAccessDeniedPageInsteadOfLoginForForbid()
    {
        var root = GetRepositoryRoot();
        var programSource = File.ReadAllText(Path.Combine(root, "src", "Wasla.Web", "Program.cs"));

        Assert.Contains("options.LoginPath = \"/auth/login\";", programSource, StringComparison.Ordinal);
        Assert.Contains("options.AccessDeniedPath = \"/auth/access-denied\";", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("options.AccessDeniedPath = \"/auth/login\";", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantAccessDeniedRouteAndView_ArePresent()
    {
        var root = GetRepositoryRoot();
        var controllerSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Controllers",
            "AuthController.cs"));
        var viewSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "Auth",
            "AccessDenied.cshtml"));

        Assert.Contains("[HttpGet(\"access-denied\")]", controllerSource, StringComparison.Ordinal);
        Assert.Contains("IActionResult AccessDenied()", controllerSource, StringComparison.Ordinal);
        Assert.Contains("Auth.AccessDenied.Title", viewSource, StringComparison.Ordinal);
        Assert.Contains("Auth.AccessDenied.Description", viewSource, StringComparison.Ordinal);
        Assert.Contains("href=\"/orders\"", viewSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Manager, TenantPolicies.CanManagePrintBridgeDevices)]
    [InlineData(UserRole.Cashier, TenantPolicies.CanViewReports)]
    [InlineData(UserRole.Viewer, TenantPolicies.CanManageOrders)]
    public async Task UnauthorizedAuthenticatedTenantRole_FailsPolicyInsteadOfSatisfyingAccess(UserRole role, string policyName)
    {
        var service = BuildAuthorizationService(_tenantId);

        var result = await service.AuthorizeAsync(
            Principal(_tenantId, role),
            null,
            policyName);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Owner_SatisfiesOwnerOnlyPolicy()
    {
        var service = BuildAuthorizationService(_tenantId);

        var result = await service.AuthorizeAsync(
            Principal(_tenantId, UserRole.Owner),
            null,
            TenantPolicies.CanManagePrintBridgeDevices);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(UserRole.Owner, true, true, true, true, true, true)]
    [InlineData(UserRole.Manager, true, true, true, false, false, false)]
    [InlineData(UserRole.Kitchen, false, true, false, false, false, false)]
    [InlineData(UserRole.Cashier, false, true, false, false, false, false)]
    [InlineData(UserRole.Viewer, true, true, false, false, false, false)]
    public async Task TenantNavigationPermissions_FollowCurrentRoleMatrix(
        UserRole role,
        bool canViewReports,
        bool canViewOrders,
        bool canManageOrderSettings,
        bool canManageTenantSettings,
        bool canManagePrintBridgeDevices,
        bool canManageTenantUsers)
    {
        var auth = BuildAuthorizationService(_tenantId);
        var navigation = new TenantNavigationAuthorizationService(auth);

        var permissions = await navigation.GetPermissionsAsync(Principal(_tenantId, role));

        Assert.Equal(canViewReports, permissions.CanViewReports);
        Assert.Equal(canViewOrders, permissions.CanViewOrders);
        Assert.Equal(canManageOrderSettings, permissions.CanManageOrderSettings);
        Assert.Equal(canManageTenantSettings, permissions.CanManageTenantSettings);
        Assert.Equal(canManagePrintBridgeDevices, permissions.CanManagePrintBridgeDevices);
        Assert.Equal(canManageTenantUsers, permissions.CanManageTenantUsers);
    }

    [Fact]
    public async Task Owner_CanViewTenantUsers()
    {
        var owner = await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        var result = await controller.Index(TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TenantUsersViewModel>(view.Model);
        var row = Assert.Single(model.Users);
        Assert.Equal(owner.Email, row.Email);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task NonOwner_CannotViewOrManageTenantUsers(UserRole role)
    {
        var service = BuildAuthorizationService(_tenantId);

        var result = await service.AuthorizeAsync(
            Principal(_tenantId, role),
            null,
            TenantPolicies.CanManageTenantUsers);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Owner_CanChangeUserRole()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner, email: "owner2@example.test");
        var user = await SeedUserAsync(_tenantId, UserRole.Viewer);
        var controller = CreateTenantUsersController();

        var result = await controller.Edit(
            user.Id,
            EditModel(user, role: UserRole.Cashier),
            TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updated = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.Equal(UserRole.Cashier, updated.Role);
    }

    [Fact]
    public async Task InvalidRole_IsRejected()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Viewer, email: "viewer@example.test");
        var controller = CreateTenantUsersController();

        var result = await controller.Edit(
            user.Id,
            EditModel(user, role: "Staff"),
            TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var unchanged = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.Equal(UserRole.Viewer, unchanged.Role);
    }

    [Fact]
    public async Task CrossTenantUserModification_IsRejected()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var otherTenantUser = await SeedUserAsync(_otherTenantId, UserRole.Viewer);
        var controller = CreateTenantUsersController();

        var result = await controller.Edit(
            otherTenantUser.Id,
            EditModel(otherTenantUser, role: UserRole.Manager),
            TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        await using var otherDb = await _db.CreateAsync(_otherTenantId, TestContext.Current.CancellationToken);
        var unchanged = await otherDb.AppUsers.SingleAsync(u => u.Id == otherTenantUser.Id, TestContext.Current.CancellationToken);
        Assert.Equal(UserRole.Viewer, unchanged.Role);
    }

    [Fact]
    public async Task Owner_CanDeactivateNonLastOwnerUser()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Manager, email: "manager@example.test");
        var controller = CreateTenantUsersController();

        var result = await controller.Deactivate(user.Id, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updated = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task Owner_CanActivateInactiveUser()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Manager, isActive: false, email: "inactive@example.test");
        var controller = CreateTenantUsersController();

        var result = await controller.Activate(user.Id, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updated = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.True(updated.IsActive);
    }

    [Fact]
    public async Task Owner_CanViewUserDetails()
    {
        var user = await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        var result = await controller.Details(user.Id, TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TenantUserDetailsViewModel>(view.Model);
        Assert.Equal(user.Email, model.Email);
        Assert.Equal(UserRole.Owner, model.Role);
    }

    [Fact]
    public async Task Owner_CanEditDisplayName()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Manager, email: "manager-name@example.test");
        var controller = CreateTenantUsersController();

        var result = await controller.Edit(
            user.Id,
            EditModel(user, fullName: "Updated Name"),
            TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updated = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Updated Name", updated.FullName);
    }

    [Fact]
    public async Task BlankPassword_KeepsExistingPasswordHash()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Manager, email: "manager-password@example.test");
        await using (var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken))
        {
            user.PasswordHash = await db.AppUsers
                .Where(u => u.Id == user.Id)
                .Select(u => u.PasswordHash)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var controller = CreateTenantUsersController();
        var result = await controller.Edit(
            user.Id,
            EditModel(user),
            TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await using var checkDb = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updatedHash = await checkDb.AppUsers
            .Where(u => u.Id == user.Id)
            .Select(u => u.PasswordHash)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(user.PasswordHash, updatedHash);
    }

    [Fact]
    public async Task NonBlankPassword_UpdatesHash()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Manager, email: "manager-new-password@example.test");
        var controller = CreateTenantUsersController();

        var result = await controller.Edit(
            user.Id,
            EditModel(user, newPassword: "new-password"),
            TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updatedHash = await db.AppUsers
            .Where(u => u.Id == user.Id)
            .Select(u => u.PasswordHash)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("new-password", updatedHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("new-password", updatedHash));
    }

    [Fact]
    public async Task PostedPasswordPlaceholder_DoesNotPersistAsRealPassword()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var user = await SeedUserAsync(_tenantId, UserRole.Manager, email: "manager-placeholder@example.test");
        await using (var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken))
        {
            user.PasswordHash = await db.AppUsers
                .Where(u => u.Id == user.Id)
                .Select(u => u.PasswordHash)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var controller = CreateTenantUsersController();
        var result = await controller.Edit(
            user.Id,
            EditModel(user, newPassword: "******"),
            TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        await using var checkDb = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var updatedHash = await checkDb.AppUsers
            .Where(u => u.Id == user.Id)
            .Select(u => u.PasswordHash)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(user.PasswordHash, updatedHash);
        Assert.False(BCrypt.Net.BCrypt.Verify("******", updatedHash));
    }

    [Fact]
    public async Task Owner_CanCreateTenantUser()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        var result = await controller.Create(new TenantUserCreateViewModel
        {
            Email = "created@example.test",
            FullName = "Created User",
            Role = UserRole.Kitchen.ToString(),
            Password = "new-password",
            ConfirmPassword = "new-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TenantUsersController.Details), redirect.ActionName);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var created = await db.AppUsers.SingleAsync(u => u.Email == "created@example.test", TestContext.Current.CancellationToken);
        Assert.Equal(UserRole.Kitchen, created.Role);
        Assert.NotEqual("new-password", created.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("new-password", created.PasswordHash));
    }

    [Fact]
    public async Task CreateUser_IsTenantScoped()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        await controller.Create(new TenantUserCreateViewModel
        {
            Email = "tenant-scoped@example.test",
            FullName = "Tenant Scoped",
            Role = UserRole.Cashier.ToString(),
            Password = "new-password",
            ConfirmPassword = "new-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        await using var tenantDb = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        await using var otherDb = await _db.CreateAsync(_otherTenantId, TestContext.Current.CancellationToken);
        Assert.True(await tenantDb.AppUsers.AnyAsync(u => u.Email == "tenant-scoped@example.test", TestContext.Current.CancellationToken));
        Assert.False(await otherDb.AppUsers.AnyAsync(u => u.Email == "tenant-scoped@example.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DuplicateEmailInSameTenant_IsRejectedAndDoesNotCreateAnotherUser()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();
        var email = "duplicate@example.test";

        await controller.Create(new TenantUserCreateViewModel
        {
            Email = email,
            FullName = "First User",
            Role = UserRole.Manager.ToString(),
            Password = "new-password",
            ConfirmPassword = "new-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        var result = await controller.Create(new TenantUserCreateViewModel
        {
            Email = email,
            FullName = "Duplicate User",
            Role = UserRole.Cashier.ToString(),
            Password = "another-password",
            ConfirmPassword = "another-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        var duplicateCount = await db.AppUsers
            .CountAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        Assert.Equal(1, duplicateCount);
    }

    [Fact]
    public async Task ActiveCreatedUser_CanLoginWithCreatedPassword()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        await controller.Create(new TenantUserCreateViewModel
        {
            Email = "created-login@example.test",
            FullName = "Created Login",
            Role = UserRole.Manager.ToString(),
            Password = "new-password",
            ConfirmPassword = "new-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        var auth = new AuthValidationService(_db);
        var session = await auth.ValidateAsync(
            _tenantId,
            "created-login@example.test",
            "new-password",
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(UserRole.Manager, session!.Role);
    }

    [Fact]
    public async Task InactiveCreatedUser_CannotLoginWithCreatedPassword()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        await controller.Create(new TenantUserCreateViewModel
        {
            Email = "created-inactive@example.test",
            FullName = "Created Inactive",
            Role = UserRole.Viewer.ToString(),
            Password = "new-password",
            ConfirmPassword = "new-password",
            IsActive = false
        }, TestContext.Current.CancellationToken);

        var auth = new AuthValidationService(_db);
        var session = await auth.ValidateAsync(
            _tenantId,
            "created-inactive@example.test",
            "new-password",
            TestContext.Current.CancellationToken);

        Assert.Null(session);
    }

    [Fact]
    public async Task PasswordConfirmationMismatch_IsRejected()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        var result = await controller.Create(new TenantUserCreateViewModel
        {
            Email = "mismatch@example.test",
            FullName = "Mismatch User",
            Role = UserRole.Viewer.ToString(),
            Password = "new-password",
            ConfirmPassword = "different-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        Assert.False(await db.AppUsers.AnyAsync(u => u.Email == "mismatch@example.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateUser_InvalidRole_IsRejected()
    {
        await SeedUserAsync(_tenantId, UserRole.Owner);
        var controller = CreateTenantUsersController();

        var result = await controller.Create(new TenantUserCreateViewModel
        {
            Email = "invalid-role@example.test",
            FullName = "Invalid Role",
            Role = "Staff",
            Password = "new-password",
            ConfirmPassword = "new-password",
            IsActive = true
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        await using var db = await _db.CreateAsync(_tenantId, TestContext.Current.CancellationToken);
        Assert.False(await db.AppUsers.AnyAsync(u => u.Email == "invalid-role@example.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TenantUsersController_RequiresTenantUserManagementPolicy()
    {
        var attribute = Assert.Single(
            typeof(TenantUsersController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>());

        Assert.Equal(TenantPolicies.CanManageTenantUsers, attribute.Policy);
    }

    [Fact]
    public void TenantUsersMutations_RequireAntiForgery()
    {
        var mutationNames = new[]
        {
            nameof(TenantUsersController.Create),
            nameof(TenantUsersController.Edit),
            nameof(TenantUsersController.Activate),
            nameof(TenantUsersController.Deactivate)
        };

        foreach (var mutationName in mutationNames)
        {
            var postMethods = typeof(TenantUsersController)
                .GetMethods()
                .Where(m => m.Name == mutationName
                    && m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Any())
                .ToArray();

            Assert.NotEmpty(postMethods);
            Assert.All(postMethods, method =>
                Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: false)));
        }
    }

    [Fact]
    public void UsersList_HasDetailsActionWithoutInlineRoleMutation()
    {
        var root = GetRepositoryRoot();
        var listSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "TenantUsers",
            "Index.cshtml"));

        Assert.Contains("asp-action=\"Details\"", listSource, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-action=\"ChangeRole\"", listSource, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"role\"", listSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantSidebar_UsesPolicyAwareNavigationPermissions()
    {
        var root = GetRepositoryRoot();
        var layoutSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "Shared",
            "_TenantLayout.cshtml"));
        var settingsSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "Shared",
            "_TenantSettingsNav.cshtml"));

        Assert.Contains("TenantNavigation.GetPermissionsAsync(User)", layoutSource, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanViewReports", layoutSource, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManagePrintBridgeDevices", layoutSource, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManageDeviceSecurity", layoutSource, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManageTenantSettings", layoutSource, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManageOrderSettings", settingsSource, StringComparison.Ordinal);
        Assert.Contains("navPermissions.CanManageTenantUsers", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderActionButtons_AreHiddenWithoutManageOrdersPolicy()
    {
        var root = GetRepositoryRoot();
        var tableSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "Orders",
            "_OrdersTable.cshtml"));

        Assert.Contains("TenantPolicies.CanManageOrders", tableSource, StringComparison.Ordinal);
        Assert.Contains("canManageOrders && o.Status", tableSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualPrintButtons_AreHiddenWithoutManualPrintPolicy()
    {
        var root = GetRepositoryRoot();
        var printJobsSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "PrintBridge",
            "_PrintJobHistory.cshtml"));

        Assert.Contains("TenantPolicies.CanManualPrint", printJobsSource, StringComparison.Ordinal);
        Assert.Contains("canManualPrint && job.CanReprint", printJobsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPage_NeverShowsCurrentPassword()
    {
        var root = GetRepositoryRoot();
        var editSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Web",
            "Areas",
            "Tenant",
            "Views",
            "TenantUsers",
            "Edit.cshtml"));

        Assert.Contains("placeholder=\"******\"", editSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordHash", editSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstProvisionedTenantUser_RemainsOwner()
    {
        var root = GetRepositoryRoot();
        var provisioningSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Wasla.Infrastructure",
            "Services",
            "TenantDatabaseProvisioningOperations.cs"));

        Assert.Contains("Role = UserRole.Owner", provisioningSource, StringComparison.Ordinal);
    }

    private TenantUsersController CreateTenantUsersController()
    {
        var controller = new TenantUsersController(
            new FixedCurrentTenantService(_tenantId),
            CreateTenantUserRoleService(),
            new FakeStringLocalizer());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.TempData = new TempDataDictionary(
            controller.ControllerContext.HttpContext,
            new NoopTempDataProvider());

        return controller;
    }

    private TenantUserRoleService CreateTenantUserRoleService() =>
        new(_db, new DefaultPasswordPolicy());

    private static TenantUserEditViewModel EditModel(
        AppUser user,
        object? role = null,
        string? fullName = null,
        string? newPassword = null) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FullName = fullName ?? user.FullName,
        Role = role?.ToString() ?? user.Role.ToString(),
        IsActive = user.IsActive,
        NewPassword = newPassword,
        ConfirmPassword = newPassword
    };

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
        var service = CreateTenantUserRoleService();

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
        var service = CreateTenantUserRoleService();

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
        var service = CreateTenantUserRoleService();

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
        var service = CreateTenantUserRoleService();

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
            AddPolicy(options, TenantPolicies.TenantOwner, UserRole.Owner);
            AddPolicy(options, TenantPolicies.TenantManagerOrOwner, UserRole.Owner, UserRole.Manager);
            AddPolicy(options, TenantPolicies.CanManageTenantUsers, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManageTenantSettings, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManagePrintBridgeDevices, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanManageDeviceSecurity, UserRole.Owner);
            AddPolicy(options, TenantPolicies.CanViewOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddPolicy(options, TenantPolicies.CanManageOrders, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanManualPrint, UserRole.Owner, UserRole.Manager, UserRole.Cashier);
            AddPolicy(options, TenantPolicies.CanViewLiveScreen, UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer);
            AddPolicy(options, TenantPolicies.CanViewReports, UserRole.Owner, UserRole.Manager, UserRole.Viewer);
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

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedCurrentTenantService : ICurrentTenantService
    {
        public FixedCurrentTenantService(Guid tenantId)
        {
            CurrentTenant = new ResolvedTenantDto(tenantId, "Tenant", "tenant", "tenant.wasla.local");
        }

        public ResolvedTenantDto? CurrentTenant { get; }
    }

    private sealed class FakeStringLocalizer : IStringLocalizer<Wasla.Web.SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class NoopTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
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
