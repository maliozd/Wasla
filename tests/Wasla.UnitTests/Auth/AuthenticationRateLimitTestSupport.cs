using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Auth;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Wraps the real <see cref="AuthValidationService"/> and <see cref="CentralAdminAuthService"/> to count how many
/// credential checks actually ran (each one is a database read and a password hash verification). With
/// <see cref="Gate"/> set, every check waits for it, so a test can hold permitted attempts open while others arrive.
/// </summary>
internal sealed class CredentialCheckProbe
{
    private int _tenantChecks;
    private int _adminChecks;

    public int TenantChecks => Volatile.Read(ref _tenantChecks);

    public int AdminChecks => Volatile.Read(ref _adminChecks);

    public TaskCompletionSource? Gate { get; set; }

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IAuthValidationService>();
        services.AddScoped<AuthValidationService>();
        services.AddScoped<IAuthValidationService>(provider => new CountingTenantChecks(provider.GetRequiredService<AuthValidationService>(), this));
        services.RemoveAll<ICentralAdminAuthService>();
        services.AddScoped<CentralAdminAuthService>();
        services.AddScoped<ICentralAdminAuthService>(provider => new CountingAdminChecks(provider.GetRequiredService<CentralAdminAuthService>(), this));
    }

    private Task EnterTenantCheckAsync()
    {
        Interlocked.Increment(ref _tenantChecks);
        return Gate?.Task ?? Task.CompletedTask;
    }

    private Task EnterAdminCheckAsync()
    {
        Interlocked.Increment(ref _adminChecks);
        return Gate?.Task ?? Task.CompletedTask;
    }

    private sealed class CountingTenantChecks(IAuthValidationService inner, CredentialCheckProbe probe) : IAuthValidationService
    {
        public async Task<AuthSessionResult?> ValidateAsync(Guid customerId, string email, string password, CancellationToken ct)
        {
            await probe.EnterTenantCheckAsync();
            return await inner.ValidateAsync(customerId, email, password, ct);
        }

        public Task<AuthSessionResult?> GetActiveSessionAsync(Guid customerId, Guid userId, CancellationToken ct) =>
            inner.GetActiveSessionAsync(customerId, userId, ct);
    }

    private sealed class CountingAdminChecks(ICentralAdminAuthService inner, CredentialCheckProbe probe) : ICentralAdminAuthService
    {
        public async Task<CentralAdminLoginResult> ValidateAsync(string email, string password, CancellationToken ct)
        {
            await probe.EnterAdminCheckAsync();
            return await inner.ValidateAsync(email, password, ct);
        }
    }
}

internal static class RateLimitTestConfig
{
    /// <summary>The same limit for every credential-entry policy.</summary>
    public static Dictionary<string, string?> Limits(int permitLimit, string window = "00:05:00")
    {
        var configuration = new Dictionary<string, string?>();
        foreach (var policy in new[] { "TenantLogin", "CentralAdminLogin", "ApiCredentialValidation" })
        {
            configuration[$"AuthenticationRateLimiting:{policy}:PermitLimit"] = permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            configuration[$"AuthenticationRateLimiting:{policy}:Window"] = window;
        }

        return configuration;
    }

    public static Dictionary<string, string?> With(this Dictionary<string, string?> configuration, string key, string? value)
    {
        configuration[key] = value;
        return configuration;
    }

    /// <summary>The test client connects from loopback, so trusting 127.0.0.1 makes it the proxy.</summary>
    public static Dictionary<string, string?> TrustingLoopbackProxy(this Dictionary<string, string?> configuration) =>
        configuration.With("TrustedProxies:KnownProxies:0", "127.0.0.1");

    public static KeyValuePair<string, string> ForwardedFor(string value) => new("X-Forwarded-For", value);

    public static int RetryAfterSeconds(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var value = Assert.Single(response.Headers.GetValues("Retry-After"));
        return int.Parse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
    }
}
