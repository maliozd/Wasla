using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Api.Security;
using Wasla.Application.Security;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// WAS-141: the complete set of Wasla authentication cookies. Wasla.Web registers the tenant and Central Admin cookie
/// schemes, Wasla.Api registers the tenant scheme only, and the name constants expose nothing else. Each assertion is
/// on the exact set, so an added cookie name or scheme fails here.
/// </summary>
public sealed class WaslaAuthCookieInventoryTests
{
    [Fact]
    public void AuthContracts_ExposeOnlyTheWaslaSchemesCookiesAndClaims()
    {
        Assert.Equal(
            new Dictionary<string, object?>
            {
                [nameof(WaslaAuthContracts.TenantScheme)] = "WaslaTenant",
                [nameof(WaslaAuthContracts.CentralAdminScheme)] = "WaslaCentralAdmin",
                [nameof(WaslaAuthContracts.TenantCookieName)] = ".Wasla.TenantAuth",
                [nameof(WaslaAuthContracts.CentralAdminCookieName)] = ".Wasla.CentralAdminAuth",
                [nameof(WaslaAuthContracts.TenantSecurityStampClaim)] = "Wasla.TenantSecurityStamp",
                [nameof(WaslaAuthContracts.CentralAdminSecurityStampClaim)] = "Wasla.CentralAdminSecurityStamp",
                [nameof(WaslaAuthContracts.TenantIdClaim)] = "TenantId"
            },
            PublicStaticValues(typeof(WaslaAuthContracts)));
    }

    [Fact]
    public void WebCookieNameConstants_ExposeOnlyTheActiveWaslaCookies()
    {
        Assert.Equal(
            new Dictionary<string, object?> { [nameof(TenantAuthCookieNames.Active)] = ".Wasla.TenantAuth" },
            PublicStaticValues(typeof(TenantAuthCookieNames)));
        Assert.Equal(
            new Dictionary<string, object?> { [nameof(CentralAdminAuthCookieNames.Active)] = ".Wasla.CentralAdminAuth" },
            PublicStaticValues(typeof(CentralAdminAuthCookieNames)));
        Assert.Equal(".Wasla.SignupRegistration", SignupRegistrationOwnership.CookieName);
    }

    [Theory]
    [InlineData(CookieSecurePolicy.Always)]
    [InlineData(CookieSecurePolicy.SameAsRequest)]
    public async Task WebAuthentication_RegistersExactlyTheTenantAndCentralAdminCookies(CookieSecurePolicy securePolicy)
    {
        // The registration Wasla.Web's Program.cs makes (checked against its source below).
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAuthentication(options =>
            {
                options.DefaultScheme = AuthSchemes.Tenant;
                options.DefaultAuthenticateScheme = AuthSchemes.Tenant;
                options.DefaultChallengeScheme = AuthSchemes.Tenant;
            })
            .AddWaslaTenantCookie(securePolicy)
            .AddWaslaCentralAdminCookie(securePolicy)
            .Services
            .BuildServiceProvider();

        var cookies = await CookieOptionsBySchemeAsync(services);

        Assert.Equal([AuthSchemes.CentralAdmin, AuthSchemes.Tenant], cookies.Keys.Order(StringComparer.Ordinal));
        AssertWaslaCookie(cookies[AuthSchemes.Tenant], ".Wasla.TenantAuth", securePolicy, TimeSpan.FromDays(7));
        AssertWaslaCookie(cookies[AuthSchemes.CentralAdmin], ".Wasla.CentralAdminAuth", securePolicy, TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task ApiAuthentication_RegistersExactlyTheTenantCookie()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddWaslaApiTenantAuthentication(CookieSecurePolicy.Always)
            .BuildServiceProvider();

        var cookies = await CookieOptionsBySchemeAsync(services);

        var (scheme, options) = Assert.Single(cookies);
        Assert.Equal(WaslaAuthContracts.TenantScheme, scheme);
        AssertWaslaCookie(options, ".Wasla.TenantAuth", CookieSecurePolicy.Always, TimeSpan.FromDays(7));
    }

    [Fact]
    public void Programs_RegisterCookiesOnlyThroughTheWaslaExtensions()
    {
        var root = RepositoryRoot();
        var web = File.ReadAllText(Path.Combine(root, "src", "Wasla.Web", "Program.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src", "Wasla.Api", "Program.cs"));

        Assert.Contains(".AddWaslaTenantCookie(authCookieSecurePolicy)", web, StringComparison.Ordinal);
        Assert.Contains(".AddWaslaCentralAdminCookie(authCookieSecurePolicy);", web, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddWaslaApiTenantAuthentication(CookieSecurePolicy.Always);", api, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddCookie(", web, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddCookie(", api, StringComparison.Ordinal);
    }

    private static void AssertWaslaCookie(
        CookieAuthenticationOptions options,
        string cookieName,
        CookieSecurePolicy securePolicy,
        TimeSpan lifetime)
    {
        Assert.Equal(cookieName, options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(securePolicy, options.Cookie.SecurePolicy);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Null(options.Cookie.Domain);
        Assert.Equal(lifetime, options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    private static async Task<Dictionary<string, CookieAuthenticationOptions>> CookieOptionsBySchemeAsync(IServiceProvider services)
    {
        var schemes = await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        var cookieOptions = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();

        Assert.All(schemes, scheme => Assert.Equal(typeof(CookieAuthenticationHandler), scheme.HandlerType));
        return schemes.ToDictionary(scheme => scheme.Name, scheme => cookieOptions.Get(scheme.Name), StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> PublicStaticValues(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(field => field.Name, field => field.GetValue(null));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }
}
