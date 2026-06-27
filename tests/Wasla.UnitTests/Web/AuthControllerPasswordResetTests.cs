using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.Models.Auth;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Web;

public sealed class AuthControllerPasswordResetTests
{
    [Fact]
    public void ForgotPasswordGet_WithTenant_RendersForm()
    {
        var controller = CreateController();

        var result = Assert.IsType<ViewResult>(controller.ForgotPassword());

        Assert.IsType<ForgotPasswordViewModel>(result.Model);
    }

    [Fact]
    public void ForgotPasswordPost_HasValidateAntiForgeryToken()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.ForgotPassword), [typeof(ForgotPasswordViewModel), typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Contains(method!.GetCustomAttributes(false), a => a is ValidateAntiForgeryTokenAttribute);
    }

    [Fact]
    public void ForgotPasswordPost_HasFocusedRateLimitPolicy()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.ForgotPassword), [typeof(ForgotPasswordViewModel), typeof(CancellationToken)]);

        var attribute = Assert.Single(method!.GetCustomAttributes(false).OfType<EnableRateLimitingAttribute>());
        Assert.Equal(RateLimitPolicies.ForgotPassword, attribute.PolicyName);
    }

    [Fact]
    public async Task ForgotPasswordPost_EmailSentShowsDedicatedConfirmationState()
    {
        var tenant = Tenant(Guid.NewGuid(), "sushim.wasla.local");
        var service = new FakeTenantPasswordResetService
        {
            RequestResult = TenantPasswordResetRequestResult.EmailSent()
        };
        var controller = CreateController(tenant: tenant, service: service, environmentName: "Development");
        controller.HttpContext.Request.Scheme = "http";
        controller.HttpContext.Request.Host = new HostString("admin.wasla.local", 5200);

        var result = await controller.ForgotPassword(
            new ForgotPasswordViewModel { Email = "owner@example.test" },
            TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ForgotPasswordViewModel>(view.Model);
        Assert.True(model.EmailSent);
        Assert.True(string.IsNullOrEmpty(model.Email));
        Assert.Equal(tenant.Id, service.LastRequestTenantId);
        Assert.Equal("owner@example.test", service.LastRequestEmail);
        Assert.Equal("http://sushim.wasla.local:5200/auth/reset-password?token=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", service.LastGeneratedResetUrl);
        Assert.Empty(controller.TempData);
    }

    [Theory]
    [InlineData(TenantPasswordResetRequestStatus.UserNotFound, "No user for this restaurant was found with this email address.")]
    [InlineData(TenantPasswordResetRequestStatus.UserInactive, "This user account is not active. Contact your restaurant administrator.")]
    [InlineData(TenantPasswordResetRequestStatus.EmailDeliveryFailed, "The password reset email could not be sent right now. Please try again later.")]
    public async Task ForgotPasswordPost_NonSentOutcomesRemainOnFormWithSafeMessage(
        TenantPasswordResetRequestStatus status,
        string expectedMessage)
    {
        var controller = CreateController(service: new FakeTenantPasswordResetService
        {
            RequestResult = status switch
            {
                TenantPasswordResetRequestStatus.UserInactive => TenantPasswordResetRequestResult.UserInactive(),
                TenantPasswordResetRequestStatus.EmailDeliveryFailed => TenantPasswordResetRequestResult.EmailDeliveryFailed(),
                _ => TenantPasswordResetRequestResult.UserNotFound()
            }
        });

        var result = await controller.ForgotPassword(
            new ForgotPasswordViewModel { Email = "owner@example.test" },
            TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ForgotPasswordViewModel>(view.Model);
        Assert.False(model.EmailSent);
        Assert.Equal("owner@example.test", model.Email);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == expectedMessage);
        Assert.Empty(controller.TempData);
    }

    [Fact]
    public async Task ForgotPasswordPost_UsesCurrentTenantNotPostedTenantId()
    {
        var currentTenant = Tenant(Guid.NewGuid(), "current.wasla.local");
        var postedTenantId = Guid.NewGuid();
        var service = new FakeTenantPasswordResetService();
        var controller = CreateController(tenant: currentTenant, service: service);
        controller.HttpContext.Request.QueryString = new QueryString($"?tenantId={postedTenantId}");

        await controller.ForgotPassword(new ForgotPasswordViewModel { Email = "owner@example.test" }, TestContext.Current.CancellationToken);

        Assert.Equal(currentTenant.Id, service.LastRequestTenantId);
        Assert.NotEqual(postedTenantId, service.LastRequestTenantId);
    }

    [Fact]
    public void ResetPasswordGet_MissingOrMalformedTokenShowsGenericInvalidState()
    {
        var controller = CreateController();

        var missing = Assert.IsType<ViewResult>(controller.ResetPassword(null));
        var missingModel = Assert.IsType<ResetPasswordViewModel>(missing.Model);
        Assert.True(missingModel.IsInvalidToken);
        Assert.True(string.IsNullOrEmpty(missingModel.Token));

        var malformed = Assert.IsType<ViewResult>(controller.ResetPassword("not-a-token"));
        var malformedModel = Assert.IsType<ResetPasswordViewModel>(malformed.Model);
        Assert.True(malformedModel.IsInvalidToken);
    }

    [Fact]
    public async Task ResetPasswordPost_ValidPostPassesCurrentTenantToService()
    {
        var tenant = Tenant(Guid.NewGuid(), "tenant.wasla.local");
        var service = new FakeTenantPasswordResetService { ResetResult = TenantPasswordResetResult.Success() };
        var controller = CreateController(tenant: tenant, service: service);

        var result = await controller.ResetPassword(new ResetPasswordViewModel
        {
            Token = FakeTenantPasswordResetService.ValidRawToken,
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        }, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AuthController.Login), redirect.ActionName);
        Assert.Equal(tenant.Id, service.LastResetTenantId);
        Assert.Equal(FakeTenantPasswordResetService.ValidRawToken, service.LastResetRawToken);
        Assert.Equal("Your password has been updated.", controller.TempData["AuthSuccess"]);
        Assert.DoesNotContain(FakeTenantPasswordResetService.ValidRawToken, controller.TempData.Values.OfType<string>());
        Assert.DoesNotContain("NewPassword123!", controller.TempData.Values.OfType<string>());
    }

    [Fact]
    public async Task ResetPasswordPost_InvalidTokenOutcomesMapToGenericError()
    {
        var controller = CreateController(service: new FakeTenantPasswordResetService
        {
            ResetResult = TenantPasswordResetResult.InvalidToken()
        });

        var result = await controller.ResetPassword(new ResetPasswordViewModel
        {
            Token = FakeTenantPasswordResetService.ValidRawToken,
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        }, TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ResetPasswordViewModel>(view.Model);
        Assert.True(model.IsInvalidToken);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "The password reset link is invalid or has expired.");
    }

    [Fact]
    public async Task ResetPasswordPost_InvalidPasswordMapsToPolicyFeedback()
    {
        var controller = CreateController(service: new FakeTenantPasswordResetService
        {
            ResetResult = TenantPasswordResetResult.InvalidPassword(["Validation.PasswordMinLength"])
        });

        var result = await controller.ResetPassword(new ResetPasswordViewModel
        {
            Token = FakeTenantPasswordResetService.ValidRawToken,
            NewPassword = "short",
            ConfirmPassword = "short"
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ViewResult>(result);
        Assert.Contains(controller.ModelState[nameof(ResetPasswordViewModel.NewPassword)]!.Errors,
            e => e.ErrorMessage == "Password must be at least 8 characters.");
    }

    [Fact]
    public void PasswordResetUrlBuilder_PreservesDevelopmentAndStagingPortsButOmitsProductionPort()
    {
        var dev = BuildRequest("http", "admin.wasla.local", 5200);
        var staging = BuildRequest("https", "admin.wasla.local", 5443);
        var production = BuildRequest("https", "admin.wasla.com", 5443);

        Assert.Equal(
            "http://tenant.wasla.local:5200/auth/reset-password?token=abc%2B%2F%3D",
            TenantWelcomeUrlBuilder.BuildPasswordResetUrl(dev, new TestWebHostEnvironment("Development"), "tenant.wasla.local", "abc+/="));
        Assert.Equal(
            "https://tenant.wasla.local:5443/auth/reset-password?token=abc",
            TenantWelcomeUrlBuilder.BuildPasswordResetUrl(staging, new TestWebHostEnvironment("Staging"), "tenant.wasla.local", "abc"));
        Assert.Equal(
            "https://tenant.wasla.com/auth/reset-password?token=abc",
            TenantWelcomeUrlBuilder.BuildPasswordResetUrl(production, new TestWebHostEnvironment("Production"), "tenant.wasla.com", "abc"));
    }

    [Fact]
    public async Task LoginPost_ValidCredentialsSignsInTenantSchemeWithPersistentClaims()
    {
        var tenant = Tenant(Guid.NewGuid(), "tenant.wasla.local");
        var userId = Guid.NewGuid();
        var authValidation = new FakeAuthValidationService
        {
            Result = new AuthSessionResult(tenant.Id, userId, "owner@example.test", "Owner User", UserRole.Owner)
        };
        var auth = new CapturingAuthenticationService();
        var controller = CreateController(tenant: tenant, authValidation: authValidation, authenticationService: auth);

        var result = await controller.Login(new LoginViewModel
        {
            Email = "owner@example.test",
            Password = "Password123!"
        }, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/dashboard", redirect.Url);
        Assert.Equal(AuthSchemes.Tenant, auth.SignInScheme);
        Assert.True(auth.SignInProperties?.IsPersistent);
        Assert.Equal(tenant.Id.ToString(), auth.SignInPrincipal?.FindFirst("TenantId")?.Value);
        Assert.Equal(userId.ToString(), auth.SignInPrincipal?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("owner@example.test", auth.SignInPrincipal?.FindFirst(ClaimTypes.Email)?.Value);
        Assert.Equal(UserRole.Owner.ToString(), auth.SignInPrincipal?.FindFirst("Role")?.Value);
        Assert.Equal(UserRole.Owner.ToString(), auth.SignInPrincipal?.FindFirst(ClaimTypes.Role)?.Value);
    }

    [Fact]
    public async Task LoginPost_ValidCredentialsExpiresLegacyCookieBeforeFreshSignIn()
    {
        var tenant = Tenant(Guid.NewGuid(), "tenant.wasla.local");
        var authValidation = new FakeAuthValidationService
        {
            Result = new AuthSessionResult(tenant.Id, Guid.NewGuid(), "owner@example.test", "Owner User", UserRole.Owner)
        };
        var controller = CreateController(tenant: tenant, authValidation: authValidation);

        await controller.Login(new LoginViewModel
        {
            Email = "owner@example.test",
            Password = "Password123!"
        }, TestContext.Current.CancellationToken);

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{TenantAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{TenantAuthCookieNames.LegacyOrderHub}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains("expires=", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_ExpiresActiveAndLegacyTenantCookies()
    {
        var auth = new CapturingAuthenticationService();
        var controller = CreateController(authenticationService: auth);

        var result = await controller.Logout();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/auth/login", redirect.Url);
        Assert.Contains(AuthSchemes.Tenant, auth.SignOutSchemes);

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{TenantAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{TenantAuthCookieNames.LegacyOrderHub}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains("expires=", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
    }

    private static AuthController CreateController(
        ResolvedTenantDto? tenant = null,
        FakeTenantPasswordResetService? service = null,
        FakeAuthValidationService? authValidation = null,
        CapturingAuthenticationService? authenticationService = null,
        string environmentName = "Development")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("tenant.wasla.local", 443);
        if (authValidation is not null || authenticationService is not null)
        {
            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(authenticationService ?? new CapturingAuthenticationService())
                .BuildServiceProvider();
        }

        var controller = new AuthController(
            new FakeCurrentTenantService(tenant ?? Tenant(Guid.NewGuid(), "tenant.wasla.local")),
            authValidation ?? new FakeAuthValidationService(),
            new FakeSignupCompletionTokenService(),
            service ?? new FakeTenantPasswordResetService(),
            new TestWebHostEnvironment(environmentName),
            new FakeStringLocalizer());

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider());
        return controller;
    }

    private static HttpRequest BuildRequest(string scheme, string host, int port)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host, port);
        return httpContext.Request;
    }

    private static ResolvedTenantDto Tenant(Guid id, string primaryDomain) => new(id, "Tenant", "tenant", primaryDomain);

    private sealed class FakeCurrentTenantService : ICurrentTenantService
    {
        public FakeCurrentTenantService(ResolvedTenantDto? tenant)
        {
            CurrentTenant = tenant;
        }

        public ResolvedTenantDto? CurrentTenant { get; }
    }

    private sealed class FakeTenantPasswordResetService : ITenantPasswordResetService
    {
        public const string ValidRawToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        public string RawTokenUsedForUrl => ValidRawToken;
        public Guid? LastRequestTenantId { get; private set; }
        public string? LastRequestEmail { get; private set; }
        public string? LastGeneratedResetUrl { get; private set; }
        public Guid? LastResetTenantId { get; private set; }
        public string? LastResetRawToken { get; private set; }
        public TenantPasswordResetRequestResult RequestResult { get; set; } =
            TenantPasswordResetRequestResult.EmailSent();
        public TenantPasswordResetResult ResetResult { get; set; } = TenantPasswordResetResult.Success();

        public Task<TenantPasswordResetRequestResult> RequestResetAsync(
            Guid tenantId,
            string email,
            TenantPasswordResetUrlFactory resetUrlFactory,
            string? cultureName = null,
            CancellationToken ct = default)
        {
            LastRequestTenantId = tenantId;
            LastRequestEmail = email;
            LastGeneratedResetUrl = resetUrlFactory(ValidRawToken);
            return Task.FromResult(RequestResult);
        }

        public Task<TenantPasswordResetResult> ResetPasswordAsync(
            Guid tenantId,
            string rawToken,
            string newPassword,
            CancellationToken ct = default)
        {
            LastResetTenantId = tenantId;
            LastResetRawToken = rawToken;
            return Task.FromResult(ResetResult);
        }
    }

    private sealed class FakeAuthValidationService : IAuthValidationService
    {
        public AuthSessionResult? Result { get; set; }

        public Task<AuthSessionResult?> ValidateAsync(Guid customerId, string email, string password, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public string? SignInScheme { get; private set; }
        public ClaimsPrincipal? SignInPrincipal { get; private set; }
        public AuthenticationProperties? SignInProperties { get; private set; }
        public List<string> SignOutSchemes { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

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
            SignInPrincipal = principal;
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

    private sealed class FakeSignupCompletionTokenService : ISignupCompletionTokenService
    {
        public string CreateToken(SignupCompletionPayload payload) => "token";

        public SignupCompletionPayload? ValidateAndConsume(string? token) => null;
    }

    private sealed class FakeStringLocalizer : IStringLocalizer<Wasla.Web.SharedResource>
    {
        private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["Auth.ForgotPassword.UserNotFound"] = "No user for this restaurant was found with this email address.",
            ["Auth.ForgotPassword.UserInactive"] = "This user account is not active. Contact your restaurant administrator.",
            ["Auth.ForgotPassword.EmailDeliveryFailed"] = "The password reset email could not be sent right now. Please try again later.",
            ["Auth.ResetPassword.Updated"] = "Your password has been updated.",
            ["Auth.ResetPassword.InvalidOrExpired"] = "The password reset link is invalid or has expired.",
            ["Validation.PasswordMinLength"] = "Password must be at least 8 characters."
        };

        public LocalizedString this[string name] => new(name, Values.GetValueOrDefault(name, name));

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, Values.GetValueOrDefault(name, name), arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Values.Select(kv => new LocalizedString(kv.Key, kv.Value));
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string ApplicationName { get; set; } = "Wasla.UnitTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
