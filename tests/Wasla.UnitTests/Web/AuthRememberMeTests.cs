using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Hosting;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Models.Auth;
using Wasla.Web.Security;
using AdminAuthController = Wasla.Web.Areas.Admin.Controllers.AuthController;
using TenantAuthController = Wasla.Web.Areas.Tenant.Controllers.AuthController;

namespace Wasla.UnitTests.Web;

public sealed class AuthRememberMeTests
{
    [Fact]
    public void AuthCookiePersistence_RememberMeFalse_IsSessionOnly()
    {
        var properties = AuthCookiePersistence.Create(rememberMe: false, TimeSpan.FromDays(14));

        Assert.False(properties.IsPersistent);
        Assert.Null(properties.ExpiresUtc);
        Assert.NotNull(properties.IssuedUtc);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void AuthCookiePersistence_RememberMeTrue_SetsPersistentExpiry(int days)
    {
        var before = DateTimeOffset.UtcNow;
        var properties = AuthCookiePersistence.Create(rememberMe: true, TimeSpan.FromDays(days));
        var after = DateTimeOffset.UtcNow;

        Assert.True(properties.IsPersistent);
        Assert.NotNull(properties.ExpiresUtc);
        Assert.InRange(
            properties.ExpiresUtc!.Value,
            before.AddDays(days).AddSeconds(-2),
            after.AddDays(days).AddSeconds(2));
    }

    [Fact]
    public void SharedResources_ContainRememberMeForSupportedCultures()
    {
        var root = FindSolutionRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.Web", "Resources");
        foreach (var file in new[]
                 {
                     "SharedResource.resx",
                     "SharedResource.tr-TR.resx",
                     "SharedResource.en-US.resx",
                     "SharedResource.ar-SA.resx",
                     "SharedResource.ru-RU.resx"
                 })
        {
            var source = File.ReadAllText(Path.Combine(resourcesDir, file));
            Assert.Contains("name=\"Auth.RememberMe\"", source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CentralAdmin_Login_RespectsRememberMe(bool rememberMe)
    {
        var authentication = new CapturingAuthenticationService();
        var controller = CreateAdminController(authentication);

        var result = await controller.Login(new AdminLoginViewModel
        {
            Email = "admin@example.test",
            Password = "Password123!",
            RememberMe = rememberMe
        }, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(AuthSchemes.CentralAdmin, authentication.SignInScheme);
        Assert.Equal(rememberMe, authentication.SignInProperties?.IsPersistent);
        if (rememberMe)
        {
            Assert.NotNull(authentication.SignInProperties?.ExpiresUtc);
            var delta = authentication.SignInProperties!.ExpiresUtc!.Value - DateTimeOffset.UtcNow;
            Assert.InRange(delta.TotalDays, 0.9, 1.1);
        }
        else
        {
            Assert.Null(authentication.SignInProperties?.ExpiresUtc);
        }
    }

    [Fact]
    public async Task CentralAdmin_LoginGet_RedirectsWhenCentralAdminAuthenticated()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "CentralAdmin")
        ], AuthSchemes.CentralAdmin));

        var authentication = new CapturingAuthenticationService
        {
            AuthenticateResults =
            {
                [AuthSchemes.CentralAdmin] = AuthenticateResult.Success(
                    new AuthenticationTicket(principal, AuthSchemes.CentralAdmin))
            }
        };
        var controller = CreateAdminController(authentication);

        var result = await controller.Login(returnUrl: null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/admin", redirect.Url);
    }

    [Fact]
    public async Task CentralAdmin_LoginGet_DoesNotRedirectForTenantPrincipal()
    {
        var tenantPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("TenantId", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, UserRole.Owner.ToString())
        ], AuthSchemes.Tenant));

        var authentication = new CapturingAuthenticationService
        {
            AuthenticateResults =
            {
                [AuthSchemes.CentralAdmin] = AuthenticateResult.NoResult(),
                [AuthSchemes.Tenant] = AuthenticateResult.Success(
                    new AuthenticationTicket(tenantPrincipal, AuthSchemes.Tenant))
            }
        };
        var controller = CreateAdminController(authentication);

        var result = await controller.Login(returnUrl: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<AdminLoginViewModel>(view.Model);
    }

    [Fact]
    public async Task CentralAdmin_Logout_DoesNotClearTenantCookie()
    {
        var authentication = new CapturingAuthenticationService();
        var controller = CreateAdminController(authentication);

        await controller.Logout();

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{CentralAdminAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{CentralAdminAuthCookieNames.LegacyOrderHub}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{CentralAdminAuthCookieNames.LegacyOrderHubScheme}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{CentralAdminAuthCookieNames.LegacyAspNetCoreOrderHubScheme}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.DoesNotContain($"{TenantAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_Login_RespectsRememberMe(bool rememberMe)
    {
        var tenant = Tenant(Guid.NewGuid(), "tenant.wasla.local");
        var authValidation = new FakeAuthValidationService
        {
            Result = new AuthSessionResult(tenant.Id, Guid.NewGuid(), "owner@example.test", "Owner", UserRole.Owner, Guid.NewGuid())
        };
        var authentication = new CapturingAuthenticationService();
        var controller = CreateTenantController(tenant, authValidation, authentication);

        var result = await controller.Login(new LoginViewModel
        {
            Email = "owner@example.test",
            Password = "Password123!",
            RememberMe = rememberMe
        }, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(AuthSchemes.Tenant, authentication.SignInScheme);
        Assert.Equal(rememberMe, authentication.SignInProperties?.IsPersistent);
        if (rememberMe)
        {
            Assert.NotNull(authentication.SignInProperties?.ExpiresUtc);
            var delta = authentication.SignInProperties!.ExpiresUtc!.Value - DateTimeOffset.UtcNow;
            Assert.InRange(delta.TotalDays, 13.9, 14.1);
        }
        else
        {
            Assert.Null(authentication.SignInProperties?.ExpiresUtc);
        }
    }

    [Fact]
    public async Task Tenant_LoginGet_RedirectsWhenTenantMatches()
    {
        var tenantId = Guid.NewGuid();
        var tenant = Tenant(tenantId, "tenant.wasla.local");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("TenantId", tenantId.ToString()),
            new Claim(ClaimTypes.Role, UserRole.Owner.ToString())
        ], AuthSchemes.Tenant));

        var authentication = new CapturingAuthenticationService
        {
            AuthenticateResults =
            {
                [AuthSchemes.Tenant] = AuthenticateResult.Success(
                    new AuthenticationTicket(principal, AuthSchemes.Tenant))
            }
        };
        var controller = CreateTenantController(tenant, authenticationService: authentication);

        var result = await controller.Login(returnUrl: null);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/dashboard", redirect.Url);
    }

    [Fact]
    public async Task Tenant_LoginGet_DoesNotRedirectOnTenantIdMismatch()
    {
        var tenant = Tenant(Guid.NewGuid(), "tenant.wasla.local");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("TenantId", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, UserRole.Owner.ToString())
        ], AuthSchemes.Tenant));

        var authentication = new CapturingAuthenticationService
        {
            AuthenticateResults =
            {
                [AuthSchemes.Tenant] = AuthenticateResult.Success(
                    new AuthenticationTicket(principal, AuthSchemes.Tenant))
            }
        };
        var controller = CreateTenantController(tenant, authenticationService: authentication);

        var result = await controller.Login(returnUrl: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<LoginViewModel>(view.Model);
    }

    [Fact]
    public async Task Tenant_LoginGet_DoesNotRedirectForCentralAdminPrincipal()
    {
        var tenant = Tenant(Guid.NewGuid(), "tenant.wasla.local");
        var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "CentralAdmin")
        ], AuthSchemes.CentralAdmin));

        var authentication = new CapturingAuthenticationService
        {
            AuthenticateResults =
            {
                [AuthSchemes.Tenant] = AuthenticateResult.NoResult(),
                [AuthSchemes.CentralAdmin] = AuthenticateResult.Success(
                    new AuthenticationTicket(adminPrincipal, AuthSchemes.CentralAdmin))
            }
        };
        var controller = CreateTenantController(tenant, authenticationService: authentication);

        var result = await controller.Login(returnUrl: null);

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Tenant_Logout_DoesNotClearCentralAdminCookie()
    {
        var authentication = new CapturingAuthenticationService();
        var controller = CreateTenantController(authenticationService: authentication);

        await controller.Logout();

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{TenantAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{TenantAuthCookieNames.LegacyOrderHub}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.DoesNotContain($"{CentralAdminAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
    }

    private static AdminAuthController CreateAdminController(CapturingAuthenticationService authentication)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(authentication)
                .BuildServiceProvider()
        };

        return new AdminAuthController(
            new SuccessfulCentralAdminAuthService(),
            new FakeStringLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };
    }

    private static TenantAuthController CreateTenantController(
        ResolvedTenantDto? tenant = null,
        FakeAuthValidationService? authValidation = null,
        CapturingAuthenticationService? authenticationService = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("tenant.wasla.local", 443);
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authenticationService ?? new CapturingAuthenticationService())
            .BuildServiceProvider();

        var controller = new TenantAuthController(
            new FakeCurrentTenantService(tenant ?? Tenant(Guid.NewGuid(), "tenant.wasla.local")),
            authValidation ?? new FakeAuthValidationService(),
            new NoopLoginRecorder(),
            new FakeSignupCompletionTokenService(),
            new FakeTenantPasswordResetService(),
            new TestWebHostEnvironment("Development"),
            new FakeStringLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider())
        };

        return controller;
    }

    private static ResolvedTenantDto Tenant(Guid id, string primaryDomain) =>
        new(id, "Tenant", "tenant", primaryDomain);

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }

    private sealed class SuccessfulCentralAdminAuthService : ICentralAdminAuthService
    {
        public Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct) =>
            Task.FromResult(new CentralAdminLoginResult(true, Guid.NewGuid(), email, "Central Admin", null));
    }

    private sealed class FakeCurrentTenantService(ResolvedTenantDto? tenant) : ICurrentTenantService
    {
        public ResolvedTenantDto? CurrentTenant { get; } = tenant;
    }

    private sealed class NoopLoginRecorder : ITenantLoginRecorder
    {
        public Task RecordSuccessfulLoginAsync(Guid tenantId, Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAuthValidationService : IAuthValidationService
    {
        public AuthSessionResult? Result { get; set; }

        public Task<AuthSessionResult?> ValidateAsync(Guid customerId, string email, string password, CancellationToken ct) =>
            Task.FromResult(Result);

        public Task<AuthSessionResult?> GetActiveSessionAsync(Guid customerId, Guid userId, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private sealed class FakeSignupCompletionTokenService : ISignupCompletionTokenService
    {
        public string CreateToken(SignupCompletionPayload payload) => "token";
        public SignupCompletionPayload? ValidateAndConsume(string? token) => null;
    }

    private sealed class FakeTenantPasswordResetService : ITenantPasswordResetService
    {
        public Task<TenantPasswordResetRequestResult> RequestResetAsync(
            Guid tenantId,
            string email,
            TenantPasswordResetUrlFactory resetUrlFactory,
            string? cultureName = null,
            CancellationToken ct = default) =>
            Task.FromResult(TenantPasswordResetRequestResult.UserNotFound());

        public Task<TenantPasswordResetResult> ResetPasswordAsync(
            Guid tenantId,
            string rawToken,
            string newPassword,
            CancellationToken ct = default) =>
            Task.FromResult(TenantPasswordResetResult.InvalidToken());
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public Dictionary<string, AuthenticateResult> AuthenticateResults { get; } = new(StringComparer.Ordinal);
        public string? SignInScheme { get; private set; }
        public AuthenticationProperties? SignInProperties { get; private set; }
        public List<string> SignOutSchemes { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            if (scheme is not null && AuthenticateResults.TryGetValue(scheme, out var result))
                return Task.FromResult(result);

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            SignInScheme = scheme;
            SignInProperties = properties;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            if (scheme is not null)
                SignOutSchemes.Add(scheme);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeStringLocalizer : IStringLocalizer<Wasla.Web.SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Wasla.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
