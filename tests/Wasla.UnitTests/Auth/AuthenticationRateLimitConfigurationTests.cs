using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Security;
using Wasla.Infrastructure.Security.RateLimiting;
using Wasla.Infrastructure.Services;
using Wasla.Web.Security;
using ApiAuthController = Wasla.Api.Controllers.AuthController;
using AdminAuthController = Wasla.Web.Areas.Admin.Controllers.AuthController;
using TenantAuthController = Wasla.Web.Areas.Tenant.Controllers.AuthController;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Credential rate-limit configuration, client normalization and trusted-proxy settings (WAS-90). The hosts must refuse
/// to start with a value that would disable the limiter or trust every proxy.
/// </summary>
public sealed class AuthenticationRateLimitConfigurationTests
{
    // --- Client network partition -------------------------------------------------------------------

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("fe80::1%7", "fe80::/64")]
    [InlineData("::1", "::/64")]
    public void ClientNetworkPartition_NormalizesAddresses(string address, string expected)
    {
        Assert.Equal(expected, ClientNetworkPartition.For(IPAddress.Parse(address)));
    }

    [Fact]
    public void ClientNetworkPartition_SeparatesDifferentNetworks_AndHasOneBucketForAMissingAddress()
    {
        Assert.NotEqual(ClientNetworkPartition.For(IPAddress.Parse("2001:db8:1:2::1")), ClientNetworkPartition.For(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.NotEqual(ClientNetworkPartition.For(IPAddress.Parse("198.51.100.7")), ClientNetworkPartition.For(IPAddress.Parse("198.51.100.8")));
        Assert.Equal(ClientNetworkPartition.Unknown, ClientNetworkPartition.For(null));
    }

    [Fact]
    public void ClientTag_IsStableWithinTheProcess_AndDoesNotContainTheAddress()
    {
        var tag = AuthenticationRateLimiting.ClientTag("198.51.100.7");

        Assert.Equal(tag, AuthenticationRateLimiting.ClientTag("198.51.100.7"));
        Assert.NotEqual(tag, AuthenticationRateLimiting.ClientTag("198.51.100.8"));
        Assert.Matches("^[0-9a-f]{12}$", tag);
    }

    // --- Limits -------------------------------------------------------------------------------------

    [Fact]
    public void Defaults_AreValid_AndConservative()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.Equal((10, TimeSpan.FromMinutes(5)), (options.TenantLogin.PermitLimit, options.TenantLogin.Window));
        Assert.Equal((5, TimeSpan.FromMinutes(5)), (options.CentralAdminLogin.PermitLimit, options.CentralAdminLogin.Window));
        Assert.Equal((10, TimeSpan.FromMinutes(5)), (options.ApiCredentialValidation.PermitLimit, options.ApiCredentialValidation.Window));
    }

    [Fact]
    public void Configuration_OverridesEachPolicy()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["AuthenticationRateLimiting:TenantLogin:PermitLimit"] = "20",
            ["AuthenticationRateLimiting:TenantLogin:Window"] = "00:15:00",
            ["AuthenticationRateLimiting:ApiCredentialValidation:PermitLimit"] = "3"
        });

        Assert.Equal((20, TimeSpan.FromMinutes(15)), (options.TenantLogin.PermitLimit, options.TenantLogin.Window));
        Assert.Equal((3, TimeSpan.FromMinutes(5)), (options.ApiCredentialValidation.PermitLimit, options.ApiCredentialValidation.Window));
        Assert.Equal(5, options.CentralAdminLogin.PermitLimit);
    }

    [Theory]
    [InlineData("TenantLogin", "PermitLimit", "0")]
    [InlineData("CentralAdminLogin", "PermitLimit", "-1")]
    [InlineData("ApiCredentialValidation", "PermitLimit", "10001")]
    [InlineData("TenantLogin", "Window", "00:00:00")]
    [InlineData("CentralAdminLogin", "Window", "-00:01:00")]
    [InlineData("ApiCredentialValidation", "Window", "00:00:00.5")]
    [InlineData("TenantLogin", "Window", "1.00:00:01")]
    public void InvalidLimits_FailValidation(string policy, string setting, string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>
        {
            [$"AuthenticationRateLimiting:{policy}:{setting}"] = value
        }));

        Assert.Contains($"AuthenticationRateLimiting:{policy}:{setting}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.2, "1")]
    [InlineData(1, "1")]
    [InlineData(1.01, "2")]
    [InlineData(300, "300")]
    public void RetryAfter_IsWholeSecondsRoundedUp(double seconds, string expected)
    {
        Assert.Equal(expected, AuthenticationRateLimiting.RetryAfterHeaderValue(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task WebHost_DoesNotStart_WithAnInvalidLimit()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => TenantSessionWebHost.StartAsync(
            configuration: new Dictionary<string, string?> { ["AuthenticationRateLimiting:TenantLogin:PermitLimit"] = "0" }));
    }

    [Fact]
    public async Task ApiHost_DoesNotStart_WithAnInvalidLimit()
    {
        var keys = Directory.CreateTempSubdirectory("wasla-rate-limit-keys-");
        await using var web = await TenantSessionWebHost.StartAsync(keys);
        try
        {
            await Assert.ThrowsAsync<OptionsValidationException>(() => TenantSessionApiHost.StartAsync(web, keys,
                new Dictionary<string, string?> { ["AuthenticationRateLimiting:ApiCredentialValidation:Window"] = "00:00:00" }));
        }
        finally
        {
            try { keys.Delete(recursive: true); } catch (IOException) { }
        }
    }

    // --- Trusted proxies ----------------------------------------------------------------------------

    [Fact]
    public void TrustedProxies_ValidEntries_ReplaceTheLoopbackDefaults()
    {
        var forwarded = ForwardedHeaders(new Dictionary<string, string?>
        {
            ["TrustedProxies:KnownProxies:0"] = "10.0.0.5",
            ["TrustedProxies:KnownProxies:1"] = "2001:db8::5",
            ["TrustedProxies:KnownNetworks:0"] = "10.1.0.0/24",
            ["TrustedProxies:KnownNetworks:1"] = "2001:db8:5::/48"
        });

        Assert.Equal(new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("2001:db8::5") }, forwarded.KnownProxies);
        Assert.Equal(new[] { "10.1.0.0/24", "2001:db8:5::/48" }, forwarded.KnownNetworks.Select(n => $"{n.Prefix}/{n.PrefixLength}"));
        Assert.Equal(1, forwarded.ForwardLimit);
        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            forwarded.ForwardedHeaders);
    }

    [Fact]
    public void TrustedProxies_NotConfigured_LeavesForwardedHeadersOff()
    {
        var forwarded = ForwardedHeaders(new Dictionary<string, string?>());

        Assert.Equal(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None, forwarded.ForwardedHeaders);
    }

    [Theory]
    [InlineData("TrustedProxies:KnownProxies:0", "0.0.0.0")]
    [InlineData("TrustedProxies:KnownProxies:0", "::")]
    [InlineData("TrustedProxies:KnownProxies:0", "proxy.internal")]
    [InlineData("TrustedProxies:KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("TrustedProxies:KnownNetworks:0", "::/0")]
    [InlineData("TrustedProxies:KnownNetworks:0", "10.0.0.0/33")]
    [InlineData("TrustedProxies:KnownNetworks:0", "10.0.0.0")]
    [InlineData("TrustedProxies:KnownNetworks:0", "10.0.0.0/-8")]
    [InlineData("TrustedProxies:ForwardLimit", "0")]
    public void TrustedProxies_InvalidOrTrustEverything_FailValidation(string key, string value)
    {
        Assert.Throws<OptionsValidationException>(() => ForwardedHeaders(new Dictionary<string, string?> { [key] = value }));
    }

    [Fact]
    public void AspNetCoreForwardedHeadersEnabled_IsRefused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TrustedProxies.AspNetCoreForwardedHeadersEnabledKey] = "true" })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWaslaTrustedProxies(configuration));
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebHost_DoesNotStart_WithATrustEverythingProxyNetwork()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => TenantSessionWebHost.StartAsync(
            configuration: new Dictionary<string, string?> { ["TrustedProxies:KnownNetworks:0"] = "0.0.0.0/0" }));
    }

    // --- Where the limits apply ---------------------------------------------------------------------

    [Fact]
    public void LimitsApply_ToTheCredentialEntryActionsOnly()
    {
        Assert.Equal(AuthenticationRateLimitPolicies.ApiCredentialValidation,
            ActionMethod<ApiAuthController>(nameof(ApiAuthController.Validate)).GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Null(ActionMethod<ApiAuthController>(nameof(ApiAuthController.Me)).GetCustomAttribute<EnableRateLimitingAttribute>());

        Assert.Equal(AuthenticationRateLimitPolicies.TenantLogin, LoginPolicy<TenantAuthController>(post: true));
        Assert.Null(LoginPolicy<TenantAuthController>(post: false));
        Assert.Equal(AuthenticationRateLimitPolicies.CentralAdminLogin, LoginPolicy<AdminAuthController>(post: true));
        Assert.Null(LoginPolicy<AdminAuthController>(post: false));

        Assert.True(AuthenticationRateLimitPolicies.IsTenantScoped(AuthenticationRateLimitPolicies.TenantLogin));
        Assert.True(AuthenticationRateLimitPolicies.IsTenantScoped(AuthenticationRateLimitPolicies.ApiCredentialValidation));
        Assert.False(AuthenticationRateLimitPolicies.IsTenantScoped(AuthenticationRateLimitPolicies.CentralAdminLogin));
    }

    [Fact]
    public void DummyHash_IsComputedOnce_WithTheDefaultWorkFactor()
    {
        var field = typeof(AuthValidationService).GetField("DummyHash", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field.IsInitOnly);
        var hash = Assert.IsType<string>(field.GetValue(null));
        Assert.False(BCrypt.Net.BCrypt.PasswordNeedsRehash(hash, 11));
        Assert.False(BCrypt.Net.BCrypt.Verify("Correct-Horse-9", hash));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static AuthenticationRateLimitOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddWaslaAuthenticationRateLimitOptions(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuthenticationRateLimitOptions>>().Value;
    }

    private static Microsoft.AspNetCore.Builder.ForwardedHeadersOptions ForwardedHeaders(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddWaslaTrustedProxies(configuration).BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<TrustedProxyOptions>>().Value;
        return provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
    }

    private static MethodInfo ActionMethod<TController>(string name) =>
        Assert.Single(typeof(TController).GetMethods(), method => method.Name == name);

    private static string? LoginPolicy<TController>(bool post)
    {
        var action = Assert.Single(typeof(TController).GetMethods(), method =>
            method.Name == "Login"
            && (post ? (object?)method.GetCustomAttribute<HttpPostAttribute>() : method.GetCustomAttribute<HttpGetAttribute>()) is not null);
        return action.GetCustomAttribute<AuthenticationRateLimitAttribute>()?.Policy;
    }
}
