using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Entities.Central;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Routing;

/// <summary>
/// WAS-66: Wasla.Api enforces the same host contract as Wasla.Web, through the real Api pipeline
/// (<see cref="TenantSessionApiHost"/>, Production) with the production domains <c>wasla.tr</c> and
/// <c>app.wasla.tr</c>. Only exactly <c>{slug}.app.wasla.tr</c> is looked up: every other shape answers the
/// unknown-tenant 404 without a CentralDb lookup, even when CentralDb holds an active tenant whose stored domain is
/// exactly that host. Health probes answer on any host; the Print Bridge API answers on valid Wasla hosts (and asks for
/// its device token there) and is a plain 404 elsewhere. All data is fake.
/// </summary>
public sealed class ApiHostContractTests : IAsyncLifetime
{
    // Active tenants stored under every shape the contract rejects, next to one valid tenant address.
    private static readonly string[] RejectedStoredDomains =
    [
        "wasla.tr", "www.wasla.tr", "app.wasla.tr",            // marketing, www and application entry
        "former.wasla.tr",                                     // the former {slug}.wasla.tr shape
        "admin.app.wasla.tr", "www.app.wasla.tr",              // reserved names
        "a.known.app.wasla.tr",                                // an extra label
        "known.app.wasla.tr.evil.example", "evilapp.wasla.tr", // look-alikes
        "evil.example", "localhost", "127.0.0.1"               // arbitrary and local hosts
    ];

    private readonly ConcurrentDictionary<string, int> _lookups = new(StringComparer.Ordinal);
    private readonly DirectoryInfo _keys = Directory.CreateTempSubdirectory("wasla-api-host-keys-");
    private TenantSessionWebHost _web = null!;
    private TenantSessionApiHost _api = null!;

    public async ValueTask InitializeAsync()
    {
        _web = await TenantSessionWebHost.StartAsync(_keys);
        using (var db = _web.Central.CreateContext())
        {
            var index = 0;
            foreach (var domain in RejectedStoredDomains.Append("known.app.wasla.tr"))
            {
                db.Tenants.Add(new Tenant
                {
                    Id = Guid.NewGuid(),
                    Name = $"Stored {domain}",
                    Slug = $"stored-{index}",
                    PrimaryDomain = domain,
                    DatabaseName = $"Wasla_ApiHost_{index++}",
                    EncryptedConnectionString = "not-a-real-secret",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            db.SaveChanges();
        }

        _api = await TenantSessionApiHost.StartAsync(
            _web,
            _keys,
            new Dictionary<string, string?>
            {
                ["Wasla:TenantOnboarding:MarketingBaseDomain"] = "wasla.tr",
                ["Wasla:TenantOnboarding:ApplicationBaseDomain"] = "app.wasla.tr"
            },
            services =>
            {
                var real = services.Single(d => d.ServiceType == typeof(ITenantResolver));
                services.Remove(real);
                services.AddScoped(real.ImplementationType!);
                services.AddScoped<ITenantResolver>(provider =>
                    new RecordingResolver((ITenantResolver)provider.GetRequiredService(real.ImplementationType!), _lookups));
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _web.DisposeAsync();
        try { _keys.Delete(recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("known.app.wasla.tr")]
    [InlineData("KNOWN.App.Wasla.TR")]
    [InlineData("known.app.wasla.tr.")]
    public async Task AKnownTenantAddress_ResolvesItsTenant(string host)
    {
        using var client = _api.Client(host);

        var response = await client.GetAsync("/api/auth/me");

        // The tenant resolved; the request is only missing a session.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["known.app.wasla.tr"], _lookups.Keys);
    }

    [Theory]
    [MemberData(nameof(RejectedHosts))]
    public async Task EveryOtherHost_IsNotATenant_EvenWithAStoredTenant_AndIsNeverLookedUp(string host)
    {
        using var client = _api.Client(host);

        foreach (var path in new[] { "/api/auth/me", "/api/orders", "/" })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Tenant not found", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Empty(_lookups);
    }

    [Theory]
    [MemberData(nameof(RejectedHosts))]
    public async Task HealthProbes_AnswerOnEveryHost(string host)
    {
        using var client = _api.Client(host);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Empty(_lookups);
    }

    [Theory]
    [InlineData("known.app.wasla.tr")]
    [InlineData("nobody.app.wasla.tr")]
    [InlineData("app.wasla.tr")]
    [InlineData("wasla.tr")]
    [InlineData("www.wasla.tr")]
    public async Task ThePrintBridgeApi_OnAValidWaslaHost_AsksForItsDeviceToken(string host)
    {
        using var client = _api.Client(host);

        var response = await client.GetAsync("/api/print-bridge/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_lookups);
    }

    [Theory]
    [InlineData("former.wasla.tr")]
    [InlineData("admin.app.wasla.tr")]
    [InlineData("known.app.wasla.tr.evil.example")]
    [InlineData("evil.example")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task ThePrintBridgeApi_OnAnyOtherHost_IsAPlainNotFound(string host)
    {
        using var client = _api.Client(host);

        var response = await client.SendAsync(
            HttpMethod.Get,
            "/api/print-bridge/health",
            content: null,
            [new("X-PrintBridge-Token", "fake-token-not-real")]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_lookups);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/print-bridgex/health")]
    public async Task PathPrefixes_MatchWholeSegments(string path)
    {
        using var client = _api.Client("evil.example");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Tenant not found", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<string> RejectedHosts()
    {
        var data = new TheoryData<string>();
        foreach (var host in RejectedStoredDomains)
            data.Add(host);
        data.Add("[::1]");
        data.Add("nobody.wasla.tr");
        return data;
    }

    private sealed class RecordingResolver(ITenantResolver inner, ConcurrentDictionary<string, int> lookups) : ITenantResolver
    {
        public Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct)
        {
            lookups.AddOrUpdate(host.Trim().ToLowerInvariant(), 1, (_, count) => count + 1);
            return inner.ResolveByHostAsync(host, ct);
        }
    }
}
