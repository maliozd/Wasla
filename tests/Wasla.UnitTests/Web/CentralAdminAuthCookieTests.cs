using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
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
    public void Program_UsesCentralAdminCookieConstantWithoutLegacyActiveConfiguration()
    {
        var root = FindSolutionRoot();
        var programSource = File.ReadAllText(Path.Combine(root, "src", "Wasla.Web", "Program.cs"));
        var schemesSource = File.ReadAllText(Path.Combine(root, "src", "Wasla.Web", "Security", "AuthSchemes.cs"));

        Assert.Contains("options.Cookie.Name = CentralAdminAuthCookieNames.Active;", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("options.Cookie.Name = \"orderhub_central_admin\";", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderHubCentralAdmin", schemesSource, StringComparison.Ordinal);
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
        Assert.Equal(
            SuccessfulCentralAdminAuthService.IssuedStamp.ToString(),
            authentication.SignInPrincipal?.FindFirstValue(WaslaAuthContracts.CentralAdminSecurityStampClaim));
    }

    [Fact]
    public void Program_RevalidatesCentralAdminSessionsOnly()
    {
        var programSource = File.ReadAllText(Path.Combine(FindSolutionRoot(), "src", "Wasla.Web", "Program.cs"));
        var tenantScheme = Section(programSource, ".AddCookie(AuthSchemes.Tenant,", ".AddCookie(AuthSchemes.CentralAdmin,");
        var adminScheme = Section(programSource, ".AddCookie(AuthSchemes.CentralAdmin,", "});");

        Assert.Contains("options.EventsType = typeof(CentralAdminCookieEvents);", adminScheme, StringComparison.Ordinal);
        Assert.DoesNotContain("EventsType", tenantScheme, StringComparison.Ordinal);
        Assert.DoesNotContain("Events =", tenantScheme, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddScoped<CentralAdminCookieEvents>();", programSource, StringComparison.Ordinal);
    }

    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found.");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'.");
        return source[from..to];
    }

    [Fact]
    public async Task Logout_ExpiresActiveAndLegacyCentralAdminCookiesWithoutTenantCookie()
    {
        var authentication = new CapturingAuthenticationService();
        var controller = CreateController(authentication);

        var result = await controller.Logout();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/admin/login", redirect.Url);
        Assert.Contains(AuthSchemes.CentralAdmin, authentication.SignOutSchemes);

        var setCookieHeaders = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{CentralAdminAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{CentralAdminAuthCookieNames.LegacyOrderHub}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{CentralAdminAuthCookieNames.LegacyOrderHubScheme}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains($"{CentralAdminAuthCookieNames.LegacyAspNetCoreOrderHubScheme}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.DoesNotContain($"{TenantAuthCookieNames.Active}=", setCookieHeaders, StringComparison.Ordinal);
        Assert.Contains("expires=", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookieHeaders, StringComparison.OrdinalIgnoreCase);
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
        public static readonly Guid IssuedStamp = Guid.NewGuid();

        public Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct) =>
            Task.FromResult(new CentralAdminLoginResult(
                true,
                Guid.NewGuid(),
                email,
                "Central Admin",
                null,
                IssuedStamp));
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
