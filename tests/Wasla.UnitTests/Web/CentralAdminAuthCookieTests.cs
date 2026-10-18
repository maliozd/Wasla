using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Security;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Security;
using AdminAuthController = Wasla.Web.Areas.Admin.Controllers.AuthController;

namespace Wasla.UnitTests.Web;

public sealed class CentralAdminAuthCookieTests
{
    [Fact]
    public void AuthConstants_UseWaslaBrandingAndPreserveTenantCookie()
    {
        Assert.Equal("WaslaCentralAdmin", AuthSchemes.CentralAdmin);
        Assert.Equal(".Wasla.CentralAdminAuth", CentralAdminAuthCookieNames.Active);
        Assert.Equal(".Wasla.TenantAuth", TenantAuthCookieNames.Active);
    }

    [Fact]
    public void Program_RegistersTheCentralAdminCookieThroughTheWaslaExtension()
    {
        var root = FindSolutionRoot();
        var programSource = File.ReadAllText(Path.Combine(root, "src", "Wasla.Web", "Program.cs"));

        Assert.Contains(".AddWaslaCentralAdminCookie(authCookieSecurePolicy)", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddCookie(AuthSchemes.CentralAdmin", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralAdminCookie_UsesWaslaCookieAndRevalidatesEverySession()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddAuthentication()
            .AddWaslaCentralAdminCookie(CookieSecurePolicy.Always)
            .Services
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthSchemes.CentralAdmin);

        Assert.Equal(CentralAdminAuthCookieNames.Active, options.Cookie.Name);
        Assert.Equal(".Wasla.CentralAdminAuth", options.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Null(options.Cookie.Domain);
        Assert.Equal("/admin/login", options.LoginPath);
        Assert.Equal(TimeSpan.FromHours(8), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal(typeof(CentralAdminCookieEvents), options.EventsType);
        Assert.True(services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(CentralAdminCookieEvents)));
    }

    [Fact]
    public async Task Login_UsesWaslaCentralAdminScheme()
    {
        var authentication = new CapturingAuthenticationService();
        var controller = CreateController(authentication);

        var result = await controller.Login(new AdminLoginViewModel
        {
            Email = "admin@example.test",
            Password = "Password123!",
            RememberMe = true
        }, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/admin", redirect.Url);
        Assert.Equal(AuthSchemes.CentralAdmin, authentication.SignInScheme);
        Assert.True(authentication.SignInProperties?.IsPersistent);
        Assert.NotNull(authentication.SignInProperties?.ExpiresUtc);

        // The session carries the account's security stamp, and reads back as a valid Central Admin session.
        Assert.True(CentralAdminSessionClaims.TryRead(authentication.SignInPrincipal, out var session));
        Assert.Equal(SuccessfulCentralAdminAuthService.UserId, session.AdminUserId);
        Assert.Equal(SuccessfulCentralAdminAuthService.SecurityStamp, session.SecurityStamp);
    }

    [Fact]
    public async Task Logout_ExpiresOnlyTheActiveCentralAdminCookie()
    {
        var authentication = new CapturingAuthenticationService();
        var controller = CreateController(authentication);

        var result = await controller.Logout();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/admin/login", redirect.Url);
        Assert.Contains(AuthSchemes.CentralAdmin, authentication.SignOutSchemes);

        var setCookie = Assert.Single(controller.Response.Headers.SetCookie)!;
        Assert.StartsWith(".Wasla.CentralAdminAuth=;", setCookie, StringComparison.Ordinal);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static AdminAuthController CreateController(CapturingAuthenticationService authentication)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(authentication)
                .BuildServiceProvider()
        };

        var controller = new AdminAuthController(
            new SuccessfulCentralAdminAuthService(),
            new FakeStringLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return controller;
    }

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
        public static readonly Guid UserId = Guid.NewGuid();
        public static readonly Guid SecurityStamp = Guid.NewGuid();

        public Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct) =>
            Task.FromResult(new CentralAdminLoginResult(
                true,
                UserId,
                email,
                "Central Admin",
                null,
                SecurityStamp));
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public string? SignInScheme { get; private set; }
        public AuthenticationProperties? SignInProperties { get; private set; }
        public ClaimsPrincipal? SignInPrincipal { get; private set; }
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
            SignInProperties = properties;
            SignInPrincipal = principal;
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
}
