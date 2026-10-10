using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Every endpoint Wasla.Api actually maps (read from the running Api host's endpoint data source, so controllers and
/// minimal endpoints are both covered) must carry a deliberate access decision. A protected endpoint must name a policy
/// registered by the Api, and that policy must admit exactly the roles the Web policy for the same operation admits,
/// evaluated through each host's own registered authorization services. A new Api action without an entry below, or
/// with a bare <c>[Authorize]</c>, fails these tests.
/// </summary>
public sealed class ApiAuthorizationContractTests : IAsyncLifetime
{
    private const string AnyAssignableRole = "(any assignable tenant role)";

    /// <summary>
    /// Each mapped endpoint ("METHOD route", "*" when the endpoint accepts any method) and either the reason it is
    /// anonymous or the Web policy whose roles it must admit.
    /// </summary>
    private static readonly Dictionary<string, Expected> Endpoints = new(StringComparer.Ordinal)
    {
        ["POST api/auth/validate"] = Expected.Anonymous("Credential check for Api clients; issues no session."),
        ["GET api/auth/me"] = Expected.Policy(AnyAssignableRole),
        ["GET api/branches"] = Expected.Policy(TenantPolicies.CanManageTenantSettings),
        ["POST api/branches"] = Expected.Policy(TenantPolicies.CanManageTenantSettings),
        ["GET api/dashboard/summary"] = Expected.Policy(TenantPolicies.CanViewReports),
        ["GET api/dashboard/today"] = Expected.Policy(TenantPolicies.CanViewReports),
        ["GET api/orders"] = Expected.Policy(TenantPolicies.CanViewOrders),
        ["GET api/orders/{id:guid}"] = Expected.Policy(TenantPolicies.CanViewOrders),
        ["GET api/platform-connections"] = Expected.Policy(TenantPolicies.CanManageTenantSettings),
        ["POST api/platform-connections"] = Expected.Policy(TenantPolicies.CanManageTenantSettings),
        ["PATCH api/platform-connections/{id:guid}/active"] = Expected.Policy(TenantPolicies.CanManageTenantSettings),
        ["GET api/print-bridge/health"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["GET api/print-bridge/jobs/pending"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["POST api/print-bridge/jobs/{jobId:guid}/mark-printing"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["PUT api/print-bridge/device/name"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["POST api/print-bridge/jobs/{jobId:guid}/mark-printed"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["POST api/print-bridge/jobs/{jobId:guid}/reprint"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["POST api/print-bridge/jobs/{jobId:guid}/mark-failed"] = Expected.Anonymous(PrintBridgeDeviceToken),
        ["GET /"] = Expected.Anonymous("Service banner; reads no tenant data."),
        ["* /health/live"] = Expected.Anonymous("Liveness probe; skips tenant resolution."),
        ["* /health/ready"] = Expected.Anonymous("Readiness probe (CentralDb only); skips tenant resolution.")
    };

    private const string PrintBridgeDeviceToken =
        "Authenticated by the X-PrintBridge-Token device token in PrintBridgeAuthMiddleware, not by the tenant cookie.";

    private static readonly UserRole[] AssignableRoles =
        [UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer];

    private DirectoryInfo _keys = null!;
    private TenantSessionWebHost _web = null!;
    private TenantSessionApiHost _api = null!;

    public async ValueTask InitializeAsync()
    {
        _keys = Directory.CreateTempSubdirectory("wasla-contract-keys-");
        _web = await TenantSessionWebHost.StartAsync(_keys);
        _api = await TenantSessionApiHost.StartAsync(_web, _keys);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _web.DisposeAsync();
        try { _keys.Delete(recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EveryMappedApiEndpoint_HasAnIntentionalAccessDecision()
    {
        var mapped = ApiEndpoints().Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        var undecided = mapped.Except(Endpoints.Keys).Order().ToList();
        var gone = Endpoints.Keys.Except(mapped).Order().ToList();

        Assert.True(undecided.Count == 0, "Api endpoints without an access decision: " + string.Join(", ", undecided));
        Assert.True(gone.Count == 0, "Access decisions for endpoints the Api no longer maps: " + string.Join(", ", gone));
    }

    [Fact]
    public void AnonymousApiEndpoints_AreExactlyTheDocumentedOnes()
    {
        foreach (var (key, endpoint) in ApiEndpoints())
        {
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            Assert.True(Endpoints[key].IsAnonymous == anonymous,
                $"{key}: expected {(Endpoints[key].IsAnonymous ? "anonymous" : "protected")}, mapped {(anonymous ? "anonymous" : "protected")}.");
        }
    }

    [Fact]
    public async Task ProtectedApiEndpoints_NameARegisteredPolicy_AndAdmitExactlyTheWebRoles()
    {
        var apiPolicies = _api.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var failures = new List<string>();

        foreach (var (key, endpoint) in ApiEndpoints().Where(e => !Endpoints[e.Key].IsAnonymous))
        {
            var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            if (authorizeData.Count == 0 || authorizeData.Any(a => string.IsNullOrWhiteSpace(a.Policy)))
            {
                failures.Add($"{key}: every [Authorize] must name a policy");
                continue;
            }

            var policy = await AuthorizationPolicy.CombineAsync(apiPolicies, authorizeData);
            Assert.NotNull(policy);
            var webPolicy = Endpoints[key].WebPolicy!;

            foreach (var role in Enum.GetValues<UserRole>())
            {
                var expected = webPolicy == AnyAssignableRole
                    ? AssignableRoles.Contains(role)
                    : await AuthorizeAsync(_web.Services, Principal(_web.AlphaId, role), webPolicy);
                var actual = await AuthorizeAsync(_api.Services, Principal(_web.AlphaId, role), policy);
                if (actual != expected)
                    failures.Add($"{key} as {role}: expected {(expected ? "allowed" : "denied")}, Api {(actual ? "allows" : "denies")}");
            }

            if (await AuthorizeAsync(_api.Services, Principal(_web.AlphaId, role: null), policy))
                failures.Add($"{key}: admits a session without a role");
            if (await AuthorizeAsync(_api.Services, Principal(_web.BetaId, UserRole.Owner), policy))
                failures.Add($"{key}: admits an Owner session of another tenant");
            if (await AuthorizeAsync(_api.Services, new ClaimsPrincipal(new ClaimsIdentity()), policy))
                failures.Add($"{key}: admits an anonymous request");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task ApiDefaultAndFallbackPolicies_AdmitOnlyACurrentOwner()
    {
        var apiPolicies = _api.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallback = await apiPolicies.GetFallbackPolicyAsync();
        var defaultPolicy = await apiPolicies.GetDefaultPolicyAsync();

        Assert.NotNull(fallback);
        foreach (var policy in new[] { fallback!, defaultPolicy })
        {
            Assert.Contains(WaslaAuthContracts.TenantScheme, policy.AuthenticationSchemes);
            foreach (var role in Enum.GetValues<UserRole>())
                Assert.Equal(role == UserRole.Owner, await AuthorizeAsync(_api.Services, Principal(_web.AlphaId, role), policy));
            Assert.False(await AuthorizeAsync(_api.Services, Principal(_web.BetaId, UserRole.Owner), policy));
            Assert.False(await AuthorizeAsync(_api.Services, new ClaimsPrincipal(new ClaimsIdentity()), policy));
        }
    }

    private IEnumerable<(string Key, RouteEndpoint Endpoint)> ApiEndpoints()
    {
        var endpoints = _api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            var method = methods is { Count: > 0 } ? string.Join(",", methods) : "*";
            yield return ($"{method} {endpoint.RoutePattern.RawText}", endpoint);
        }
    }

    /// <summary>A tenant session principal as the cookie scheme presents it after authentication.</summary>
    private static ClaimsPrincipal Principal(Guid tenantId, UserRole? role)
    {
        var claims = new List<Claim>
        {
            new(WaslaAuthContracts.TenantIdClaim, tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Value.ToString()));
            claims.Add(new Claim("Role", role.Value.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, WaslaAuthContracts.TenantScheme));
    }

    private Task<bool> AuthorizeAsync(IServiceProvider services, ClaimsPrincipal user, string policyName) =>
        InTenantRequestAsync(services, async auth => (await auth.AuthorizeAsync(user, resource: null, policyName)).Succeeded);

    private Task<bool> AuthorizeAsync(IServiceProvider services, ClaimsPrincipal user, AuthorizationPolicy policy) =>
        InTenantRequestAsync(services, async auth => (await auth.AuthorizeAsync(user, resource: null, policy)).Succeeded);

    /// <summary>Evaluates in a request scope whose resolved tenant is alpha, as tenant resolution would set it.</summary>
    private async Task<bool> InTenantRequestAsync(IServiceProvider services, Func<IAuthorizationService, Task<bool>> evaluate)
    {
        using var scope = services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Items["CurrentTenant"] = new ResolvedTenantDto(_web.AlphaId, "Alpha", "alpha", TenantSessionWebHost.AlphaHost);
        accessor.HttpContext = context;
        try
        {
            return await evaluate(scope.ServiceProvider.GetRequiredService<IAuthorizationService>());
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private sealed record Expected(string? WebPolicy, string? AnonymousReason)
    {
        public bool IsAnonymous => AnonymousReason is not null;

        public static Expected Anonymous(string reason) => new(null, reason);

        public static Expected Policy(string webPolicy) => new(webPolicy, null);
    }
}
