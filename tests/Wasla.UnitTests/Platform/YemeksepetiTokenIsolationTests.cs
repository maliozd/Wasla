using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.Yemeksepeti;
using Wasla.UnitTests.Diagnostics;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-88: one Yemeksepeti client instance serves every tenant in a process, so a cached OAuth token must never be
/// handed to another platform connection or to other credentials. These tests drive the public
/// <see cref="YemeksepetiFoodPlatformClient.FetchOrdersAsync"/> boundary against a fake provider that issues one
/// token per accepted credential pair and returns the orders of the token's owner, so a reused token shows up as
/// another tenant's customer data. Every credential is fake and nothing leaves the process.
/// </summary>
public sealed class YemeksepetiTokenIsolationTests
{
    private const string SharedClientId = "shared-client-id";

    private static readonly OrderFetchWindow Window = new(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

    [Fact]
    public async Task SameClientId_WrongSecret_IsRejected_AndNeverReceivesAnotherConnectionsToken()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);

        var tenantA = await client.FetchOrdersAsync(Connection(SharedClientId, "secret-of-tenant-a"), Window, CancellationToken.None);
        Assert.Equal(["tenant-a-order"], tenantA.Select(o => o.ExternalOrderId).ToArray());

        // Tenant B knows the client id but not the secret. The provider must be asked, and must refuse.
        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(Connection(SharedClientId, "guessed-secret-of-tenant-b"), Window, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("Token", ex.Operation);
        Assert.Equal(2, provider.TokenRequests);
        Assert.Equal(["tenant-a"], provider.OrdersServedTo.Distinct().ToArray());
    }

    [Fact]
    public async Task SameClientId_DifferentSecrets_EachConnectionGetsOnlyItsOwnOrders()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        provider.Register(SharedClientId, "secret-of-tenant-b", "tenant-b");
        var client = Create(provider);

        var tenantA = await client.FetchOrdersAsync(Connection(SharedClientId, "secret-of-tenant-a"), Window, CancellationToken.None);
        var tenantB = await client.FetchOrdersAsync(Connection(SharedClientId, "secret-of-tenant-b"), Window, CancellationToken.None);

        Assert.Equal(["tenant-a-order"], tenantA.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(["tenant-b-order"], tenantB.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal("Customer of tenant-b", Assert.Single(tenantB).CustomerName);
        Assert.Equal(2, provider.TokenRequests);
    }

    [Fact]
    public async Task IdenticalCredentials_OnTwoConnections_DoNotShareACachedToken()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "same-secret", "shared-principal");
        var client = Create(provider);
        var first = Connection(SharedClientId, "same-secret");
        var second = Connection(SharedClientId, "same-secret");

        await client.FetchOrdersAsync(first, Window, CancellationToken.None);
        await client.FetchOrdersAsync(second, Window, CancellationToken.None);
        await client.FetchOrdersAsync(first, Window, CancellationToken.None);
        await client.FetchOrdersAsync(second, Window, CancellationToken.None);

        // One token per connection, each reused only by the connection that acquired it.
        Assert.Equal(2, provider.TokenRequests);
        var bearers = provider.OrderBearers.ToArray();
        Assert.Equal(4, bearers.Length);
        Assert.NotEqual(bearers[0], bearers[1]);
        Assert.Equal(bearers[0], bearers[2]);
        Assert.Equal(bearers[1], bearers[3]);
    }

    [Fact]
    public async Task SameConnection_SameCredentials_ReusesTheUnexpiredToken()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(1, provider.TokenRequests);
        Assert.Single(provider.OrderBearers.Distinct());
    }

    [Fact]
    public async Task Token_IsReusedUntilFiveMinutesBeforeExpiry_ThenRefreshed()
    {
        var clock = new LimiterClock();
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider, time: clock);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        // expires_in is 7200 seconds; the token is still reused just before the 5-minute refresh margin.
        clock.Now += TimeSpan.FromMinutes(114);
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        Assert.Equal(1, provider.TokenRequests);

        clock.Now += TimeSpan.FromMinutes(2);
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(2, provider.TokenRequests);
        var bearers = provider.OrderBearers.ToArray();
        Assert.Equal(bearers[0], bearers[1]);
        Assert.NotEqual(bearers[1], bearers[2]);
        Assert.Equal(bearers[2], bearers[3]);
    }

    [Fact]
    public async Task ExpiredEntries_ArePruned_WhenANewTokenIsAcquired()
    {
        var clock = new LimiterClock();
        var provider = new FakeProvider();
        var client = Create(provider, time: clock);
        for (var i = 0; i < 3; i++)
        {
            provider.Register($"client-{i}", $"secret-{i}", $"tenant-{i}");
            await client.FetchOrdersAsync(Connection($"client-{i}", $"secret-{i}"), Window, CancellationToken.None);
        }

        Assert.Equal(3, client.CachedTokenCount);

        // Rotated credentials and removed connections leave entries behind only until their token expires.
        clock.Now += TimeSpan.FromHours(3);
        provider.Register("client-new", "secret-new", "tenant-new");
        await client.FetchOrdersAsync(Connection("client-new", "secret-new"), Window, CancellationToken.None);

        Assert.Equal(1, client.CachedTokenCount);
    }

    [Fact]
    public async Task TokenCache_NeverGrowsPastItsBound()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "same-secret", "shared-principal");
        var client = Create(provider);

        for (var i = 0; i <= YemeksepetiFoodPlatformClient.MaxCachedTokens; i++)
            await client.FetchOrdersAsync(Connection(SharedClientId, "same-secret"), Window, CancellationToken.None);

        Assert.Equal(YemeksepetiFoodPlatformClient.MaxCachedTokens, client.CachedTokenCount);
        Assert.Equal(YemeksepetiFoodPlatformClient.MaxCachedTokens + 1, provider.TokenRequests);
    }

    [Fact]
    public async Task RotatedSecret_AcquiresANewToken_AndNeverSendsTheOldOne()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "old-secret", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "old-secret");

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        var oldToken = Assert.Single(provider.OrderBearers);

        provider.Register(SharedClientId, "new-secret", "tenant-a");
        connection.EncryptedApiSecret = "new-secret";
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(2, provider.TokenRequests);
        Assert.NotEqual(oldToken, provider.OrderBearers.Last());
    }

    [Fact]
    public async Task SecretRotatedToAnInvalidValue_FailsInsteadOfUsingTheCachedToken()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "old-secret", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "old-secret");

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        connection.EncryptedApiSecret = "mistyped-secret";

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(connection, Window, CancellationToken.None));

        Assert.Equal("Token", ex.Operation);
        Assert.Single(provider.OrderBearers);
    }

    [Fact]
    public async Task ConcurrentFetches_ForOneConnection_ShareOneTokenRequest()
    {
        var provider = new FakeProvider { HoldTokenResponses = true };
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");

        var fetches = Enumerable.Range(0, 8)
            .Select(_ => client.FetchOrdersAsync(connection, Window, CancellationToken.None))
            .ToArray();
        await provider.FirstTokenRequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        provider.ReleaseTokenResponses();
        var results = await Task.WhenAll(fetches).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.TokenRequests);
        Assert.All(results, orders => Assert.Equal(["tenant-a-order"], orders.Select(o => o.ExternalOrderId).ToArray()));
    }

    [Fact]
    public async Task ConcurrentFetches_ForManyConnections_NeverSwapTokensOrOrders()
    {
        var provider = new FakeProvider { HoldTokenResponses = true };
        var connections = Enumerable.Range(0, 12)
            .Select(i =>
            {
                // Half of the tenants share one client id with different secrets.
                var clientId = i % 2 == 0 ? SharedClientId : $"client-{i}";
                provider.Register(clientId, $"secret-{i}", $"tenant-{i}");
                return (Principal: $"tenant-{i}", Connection: Connection(clientId, $"secret-{i}"));
            })
            .ToArray();
        var client = Create(provider);

        var fetches = connections
            .SelectMany(c => Enumerable.Range(0, 3).Select(_ => (c.Principal, Task: client.FetchOrdersAsync(c.Connection, Window, CancellationToken.None))))
            .ToArray();
        await provider.FirstTokenRequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        provider.ReleaseTokenResponses();
        await Task.WhenAll(fetches.Select(f => f.Task)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        foreach (var (principal, task) in fetches)
        {
            var order = Assert.Single(await task);
            Assert.Equal($"{principal}-order", order.ExternalOrderId);
            Assert.Equal($"Customer of {principal}", order.CustomerName);
        }

        Assert.Equal(connections.Length, provider.TokenRequests);
    }

    [Fact]
    public async Task FailedTokenRequest_ForOneConnection_DoesNotAffectAnother()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var tenantA = Connection(SharedClientId, "secret-of-tenant-a");
        var tenantB = Connection(SharedClientId, "wrong-secret");

        await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(tenantB, Window, CancellationToken.None));
        var ordersA = await client.FetchOrdersAsync(tenantA, Window, CancellationToken.None);
        await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(tenantB, Window, CancellationToken.None));
        var ordersAAgain = await client.FetchOrdersAsync(tenantA, Window, CancellationToken.None);

        Assert.Equal(["tenant-a-order"], ordersA.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(["tenant-a-order"], ordersAAgain.Select(o => o.ExternalOrderId).ToArray());
        // B asked the provider both times; A's single token was reused only by A.
        Assert.Equal(3, provider.TokenRequests);
        Assert.Equal(["tenant-a"], provider.OrdersServedTo.Distinct().ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedToken_OnTheOrdersEndpoint_IsEvicted_SoTheNextFetchAcquiresANewOne(HttpStatusCode rejection)
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        provider.RevokeIssuedTokens(rejection);

        var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(connection, Window, CancellationToken.None));
        Assert.Equal(rejection, ex.StatusCode);

        var orders = await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(["tenant-a-order"], orders.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(2, provider.TokenRequests);
    }

    [Fact]
    public async Task OtherOrdersFailures_KeepTheCachedToken()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        provider.NextOrdersStatus = HttpStatusCode.InternalServerError;
        await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(connection, Window, CancellationToken.None));
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(1, provider.TokenRequests);
    }

    [Fact]
    public async Task LogsAndExceptions_NeverContainSecretsOrTokens()
    {
        var logger = new CollectingLogger<YemeksepetiFoodPlatformClient>();
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider, logger);
        var tenantA = Connection(SharedClientId, "secret-of-tenant-a");
        var exceptions = new List<Exception>();

        await client.FetchOrdersAsync(tenantA, Window, CancellationToken.None);
        exceptions.Add(await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(Connection(SharedClientId, "wrong-secret"), Window, CancellationToken.None)));
        provider.RevokeIssuedTokens(HttpStatusCode.Unauthorized);
        exceptions.Add(await Assert.ThrowsAsync<ProviderRequestException>(() =>
            client.FetchOrdersAsync(tenantA, Window, CancellationToken.None)));
        await client.FetchOrdersAsync(tenantA, Window, CancellationToken.None);

        var forbidden = new[] { "secret-of-tenant-a", "wrong-secret", SharedClientId }
            .Concat(provider.IssuedTokens)
            .ToArray();
        Assert.NotEmpty(logger.Entries);
        foreach (var value in forbidden)
        {
            Assert.All(logger.Entries, entry => Assert.DoesNotContain(value, entry.Message, StringComparison.Ordinal));
            Assert.All(exceptions, ex => Assert.DoesNotContain(value, ex.ToString(), StringComparison.Ordinal));
        }
    }

    // ── Failed and cancelled acquisitions (WAS-88 review) ───────────────────────────────────────────────────────
    // A token acquisition registers a cache entry before it asks the provider. These tests prove that a failed or
    // cancelled acquisition does not leave that entry behind, so callers cannot grow the cache without bound.

    [Fact]
    public async Task ThousandsOfFailingCredentials_LeaveNoCacheEntries()
    {
        var provider = new FakeProvider();
        var client = Create(provider);
        const int attempts = YemeksepetiFoodPlatformClient.MaxCachedTokens + 1000;

        for (var i = 0; i < attempts; i++)
        {
            var ex = await Assert.ThrowsAsync<ProviderRequestException>(() =>
                client.FetchOrdersAsync(Connection($"client-{i}", $"wrong-secret-{i}"), Window, CancellationToken.None));
            Assert.Equal("Token", ex.Operation);
        }

        Assert.Equal(attempts, provider.TokenRequests);
        Assert.Equal(0, client.CachedTokenCount);
    }

    [Fact]
    public async Task PreCancelledFetch_LeavesNoCacheEntry_AndMakesNoRequest()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchOrdersAsync(Connection(SharedClientId, "secret-of-tenant-a"), Window, cts.Token));

        Assert.Equal(0, provider.TokenRequests);
        Assert.Equal(0, client.CachedTokenCount);
    }

    [Fact]
    public async Task WaiterCancelledBehindAnInFlightRequest_DoesNotDisturbTheOwnersToken()
    {
        var provider = new FakeProvider { HoldTokenResponses = true };
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");
        using var waiterCts = new CancellationTokenSource();

        var owner = client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        await provider.FirstTokenRequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var waiter = client.FetchOrdersAsync(connection, Window, waiterCts.Token);
        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        provider.ReleaseTokenResponses();
        var ownerOrders = await owner.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var laterOrders = await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(["tenant-a-order"], ownerOrders.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(["tenant-a-order"], laterOrders.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(1, provider.TokenRequests);
        Assert.Single(provider.OrderBearers.Distinct());
        Assert.Equal(1, client.CachedTokenCount);
    }

    [Fact]
    public async Task WaiterCancelledWhileTheOwnerFails_LeavesNoCacheEntry()
    {
        var provider = new FakeProvider { HoldTokenResponses = true };
        var client = Create(provider);
        var connection = Connection(SharedClientId, "wrong-secret");
        using var waiterCts = new CancellationTokenSource();

        var owner = client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        await provider.FirstTokenRequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var waiter = client.FetchOrdersAsync(connection, Window, waiterCts.Token);
        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        provider.ReleaseTokenResponses();
        await Assert.ThrowsAsync<ProviderRequestException>(() => owner.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(1, provider.TokenRequests);
        Assert.Equal(0, client.CachedTokenCount);
    }

    [Fact]
    public async Task OwnerCancelledDuringItsTokenRequest_LeavesNoCacheEntry_AndAWaiterStillGetsAToken()
    {
        var provider = new FakeProvider { HoldTokenResponses = true };
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");
        using var ownerCts = new CancellationTokenSource();

        var owner = client.FetchOrdersAsync(connection, Window, ownerCts.Token);
        await provider.FirstTokenRequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var waiter = client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        ownerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);

        provider.ReleaseTokenResponses();
        var waiterOrders = await waiter.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["tenant-a-order"], waiterOrders.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(2, provider.TokenRequests);
        Assert.Equal(1, client.CachedTokenCount);
    }

    [Fact]
    public async Task MixedSuccessesAndFailures_StayWithinTheBound()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "same-secret", "shared-principal");
        var client = Create(provider);
        const int rounds = YemeksepetiFoodPlatformClient.MaxCachedTokens + 50;

        for (var i = 0; i < rounds; i++)
        {
            var orders = await client.FetchOrdersAsync(Connection(SharedClientId, "same-secret"), Window, CancellationToken.None);
            Assert.Equal(["shared-principal-order"], orders.Select(o => o.ExternalOrderId).ToArray());
            await Assert.ThrowsAsync<ProviderRequestException>(() =>
                client.FetchOrdersAsync(Connection($"client-{i}", "wrong-secret"), Window, CancellationToken.None));
        }

        Assert.Equal(2 * rounds, provider.TokenRequests);
        Assert.Equal(YemeksepetiFoodPlatformClient.MaxCachedTokens, client.CachedTokenCount);
    }

    [Fact]
    public async Task ConcurrentFailingCredentials_LeaveNoCacheEntries()
    {
        var provider = new FakeProvider { HoldTokenResponses = true };
        var client = Create(provider);
        using var cancelled = new CancellationTokenSource();

        var failing = Enumerable.Range(0, 200)
            .Select(i => client.FetchOrdersAsync(Connection($"client-{i}", "wrong-secret"), Window, CancellationToken.None))
            .ToArray();
        var cancelling = Enumerable.Range(0, 50)
            .Select(i => client.FetchOrdersAsync(Connection($"cancel-{i}", "wrong-secret"), Window, cancelled.Token))
            .ToArray();
        await provider.FirstTokenRequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Every acquisition is in flight at once: at most one entry each.
        Assert.InRange(client.CachedTokenCount, 1, failing.Length + cancelling.Length);

        cancelled.Cancel();
        provider.ReleaseTokenResponses();
        foreach (var fetch in failing)
            await Assert.ThrowsAsync<ProviderRequestException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        foreach (var fetch in cancelling)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.CachedTokenCount);
    }

    [Fact]
    public async Task ParallelSuccessFailureAndCancellation_OnTheSameKeys_LeaveOneTokenPerValidConnectionOnly()
    {
        const int rounds = 40;
        var provider = new FakeProvider();
        var client = Create(provider);
        var valid = new List<(string Principal, PlatformConnection Connection)>();

        for (var round = 0; round < rounds; round++)
        {
            var connections = Enumerable.Range(0, 4)
                .Select(i =>
                {
                    var principal = $"tenant-{round}-{i}";
                    var isValid = i % 2 == 0;
                    if (isValid)
                        provider.Register(SharedClientId, $"secret-{round}-{i}", principal);
                    return (Principal: principal, IsValid: isValid, Connection: Connection(SharedClientId, $"secret-{round}-{i}"));
                })
                .ToArray();
            valid.AddRange(connections.Where(c => c.IsValid).Select(c => (c.Principal, c.Connection)));

            var fetches = connections
                .SelectMany(c => Enumerable.Range(0, 9).Select(n => (c.Principal, c.IsValid, Cancel: n % 3 == 0, c.Connection)))
                .Select(f => (f.Principal, f.IsValid, f.Cancel, Task: Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource();
                    if (f.Cancel)
                        cts.CancelAfter(TimeSpan.FromTicks(Random.Shared.Next(0, 2000)));
                    return await client.FetchOrdersAsync(f.Connection, Window, cts.Token);
                }, TestContext.Current.CancellationToken)))
                .ToArray();

            foreach (var f in fetches)
            {
                try
                {
                    var orders = await f.Task;
                    Assert.True(f.IsValid);
                    Assert.Equal([$"{f.Principal}-order"], orders.Select(o => o.ExternalOrderId).ToArray());
                }
                catch (OperationCanceledException) when (f.Cancel)
                {
                }
                catch (ProviderRequestException ex) when (!f.IsValid)
                {
                    Assert.Equal("Token", ex.Operation);
                }
            }
        }

        // Only the valid connections keep an entry: no failed or cancelled acquisition left one behind.
        Assert.Equal(valid.Count, client.CachedTokenCount);
        var before = provider.TokenRequests;
        foreach (var (principal, connection) in valid)
        {
            var orders = await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
            Assert.Equal([$"{principal}-order"], orders.Select(o => o.ExternalOrderId).ToArray());
        }

        Assert.Equal(before, provider.TokenRequests);
    }

    [Fact]
    public async Task StaleRejection_DoesNotRemoveANewerReplacementToken()
    {
        var clock = new LimiterClock();
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider, time: clock);
        var connection = Connection(SharedClientId, "secret-of-tenant-a");

        // Fetch 1 sends the first token to the orders endpoint and is held there.
        var (ordersStarted, releaseOrders) = provider.HoldNextOrdersResponse();
        var stale = client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        await ordersStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var firstToken = Assert.Single(provider.OrderBearers);

        // Meanwhile the first token reaches its refresh margin and fetch 2 replaces it.
        clock.Now += TimeSpan.FromMinutes(116);
        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);
        var replacement = provider.OrderBearers.Last();
        Assert.NotEqual(firstToken, replacement);

        // The provider now rejects the first token; the held fetch 1 receives that late 401.
        provider.RevokeToken(firstToken, HttpStatusCode.Unauthorized);
        releaseOrders();
        await Assert.ThrowsAsync<ProviderRequestException>(() => stale.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await client.FetchOrdersAsync(connection, Window, CancellationToken.None);

        Assert.Equal(2, provider.TokenRequests);
        Assert.Equal(replacement, provider.OrderBearers.Last());
        Assert.Equal(1, client.CachedTokenCount);
    }

    [Fact]
    public async Task OneConnectionsFailure_NeverRemovesAnotherConnectionsToken()
    {
        var provider = new FakeProvider();
        provider.Register(SharedClientId, "secret-of-tenant-a", "tenant-a");
        var client = Create(provider);
        var tenantA = Connection(SharedClientId, "secret-of-tenant-a");

        await client.FetchOrdersAsync(tenantA, Window, CancellationToken.None);
        for (var i = 0; i < 20; i++)
        {
            await Assert.ThrowsAsync<ProviderRequestException>(() =>
                client.FetchOrdersAsync(Connection(SharedClientId, $"wrong-secret-{i}"), Window, CancellationToken.None));
        }

        var again = await client.FetchOrdersAsync(tenantA, Window, CancellationToken.None);

        Assert.Equal(["tenant-a-order"], again.Select(o => o.ExternalOrderId).ToArray());
        Assert.Equal(21, provider.TokenRequests);
        Assert.Single(provider.OrderBearers.Distinct());
        Assert.Equal(1, client.CachedTokenCount);
    }

    private static YemeksepetiFoodPlatformClient Create(
        FakeProvider provider,
        ILogger<YemeksepetiFoodPlatformClient>? logger = null,
        TimeProvider? time = null)
    {
        var http = new HttpClient(provider) { BaseAddress = new Uri("https://yemeksepeti.example/") };
        return new YemeksepetiFoodPlatformClient(
            new SingleClientFactory(http),
            new PassthroughSecrets(),
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            Options.Create(new YemeksepetiOptions
            {
                BaseUrl = "https://yemeksepeti.example/",
                TokenPath = "/v2/oauth/token"
            }),
            logger ?? NullLogger<YemeksepetiFoodPlatformClient>.Instance,
            time);
    }

    // PassthroughSecrets returns the stored value as the decrypted one, so these fields hold the fake credentials.
    private static PlatformConnection Connection(string clientId, string clientSecret) => new()
    {
        Id = Guid.NewGuid(),
        Platform = FoodPlatform.Yemeksepeti,
        StoreId = "vendor-1",
        SupplierId = "chain-1",
        EncryptedApiKey = clientId,
        EncryptedApiSecret = clientSecret,
        IsActive = true,
        SyncIntervalSeconds = 0
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Stands in for the provider: a token is issued only for a registered client id + secret pair, and the orders
    /// endpoint answers with the orders of the principal the bearer token was issued to.
    /// </summary>
    private sealed class FakeProvider : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<(string ClientId, string Secret), string> _principals = new();
        private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, HttpStatusCode> _revoked = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _firstTokenRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseTokens = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _tokenRequests;
        private int _issued;

        public bool HoldTokenResponses { get; init; }

        public HttpStatusCode? NextOrdersStatus { get; set; }

        public int TokenRequests => Volatile.Read(ref _tokenRequests);

        public Task FirstTokenRequestStarted => _firstTokenRequest.Task;

        public ConcurrentQueue<string> OrderBearers { get; } = new();

        public ConcurrentQueue<string> OrdersServedTo { get; } = new();

        public IReadOnlyCollection<string> IssuedTokens => _tokens.Keys.ToArray();

        public void Register(string clientId, string secret, string principal) => _principals[(clientId, secret)] = principal;

        public void ReleaseTokenResponses() => _releaseTokens.TrySetResult();

        public void RevokeIssuedTokens(HttpStatusCode status)
        {
            foreach (var token in _tokens.Keys)
                _revoked[token] = status;
        }

        public void RevokeToken(string token, HttpStatusCode status) => _revoked[token] = status;

        /// <summary>
        /// Holds the next orders request until released. The response is decided after the release, so a revocation
        /// made while it is held applies to it.
        /// </summary>
        public (Task Started, Action Release) HoldNextOrdersResponse()
        {
            var hold = new OrdersHold();
            Volatile.Write(ref _ordersHold, hold);
            return (hold.Started.Task, () => hold.Released.TrySetResult());
        }

        private OrdersHold? _ordersHold;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
                return await TokenAsync(request, cancellationToken);

            OrderBearers.Enqueue(request.Headers.Authorization?.Parameter ?? string.Empty);
            if (Interlocked.Exchange(ref _ordersHold, null) is { } hold)
            {
                hold.Started.TrySetResult();
                await hold.Released.Task.WaitAsync(cancellationToken);
            }

            return Orders(request);
        }

        private sealed class OrdersHold
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _tokenRequests);
            _firstTokenRequest.TrySetResult();
            if (HoldTokenResponses)
                await _releaseTokens.Task.WaitAsync(ct);

            var form = (await request.Content!.ReadAsStringAsync(ct))
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

            if (!_principals.TryGetValue((form["client_id"], form["client_secret"]), out var principal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"invalid_client"}""")
                };
            }

            var token = $"bearer-{principal}-{Interlocked.Increment(ref _issued)}";
            _tokens[token] = principal;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":7200}""")
            };
        }

        private HttpResponseMessage Orders(HttpRequestMessage request)
        {
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;

            if (NextOrdersStatus is { } forced)
            {
                NextOrdersStatus = null;
                return new HttpResponseMessage(forced);
            }

            if (_revoked.TryGetValue(bearer, out var rejection))
                return new HttpResponseMessage(rejection);

            if (!_tokens.TryGetValue(bearer, out var principal))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            OrdersServedTo.Enqueue(principal);
            var order = new
            {
                id = $"{principal}-order",
                code = $"{principal}-order",
                status = "RECEIVED",
                total_price = 10,
                customer = new { name = $"Customer of {principal}", phone = "5550000000" }
            };
            var body = JsonSerializer.Serialize(new { page = 0, total_pages = 1, total_count = 1, data = new[] { order } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
