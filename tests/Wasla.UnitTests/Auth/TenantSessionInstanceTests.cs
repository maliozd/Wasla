using System.Net;
using Wasla.Domain.Enums;
using Wasla.Web.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Tenant logout across Web instances and across a host recreation (WAS-114). Each host is built independently: its own
/// <c>WebApplication</c>, service provider and services, over one <see cref="TenantSessionStorage"/> (the same SQLite
/// CentralDb and tenant database files) and one Data Protection key folder, as several Web instances, or a restarted
/// Web, behind one deployment with shared databases and a shared key ring. A host recreation disposes the host and its
/// service provider and builds a new one in this test process; it is not an operating-system process restart.
/// Cookies are kept in memory only.
/// </summary>
public sealed class TenantSessionInstanceTests
{
    [Fact]
    public async Task TwoWebInstances_AcceptTheSameSession_AndALogoutOnOneEndsItOnTheOther()
    {
        using var storage = new TenantSessionStorage();
        await using var a = await TenantSessionWebHost.StartAsync(storage.Keys, storage: storage);
        var owner = a.SeedUser(storage.AlphaId, "owner@alpha.test", UserRole.Owner);
        using var browser = a.Client(TenantSessionWebHost.AlphaHost);
        await browser.LoginAsync(owner.Email);
        var copied = browser.AuthCookie!;
        await using var b = await TenantSessionWebHost.StartAsync(storage.Keys, storage: storage);
        Assert.NotSame(a.Services, b.Services);

        // Before logout instance B accepts A's session; B also issues a session of its own to the same user.
        using (var onB = CookieClient(b, copied))
            Assert.Equal(HttpStatusCode.OK, (await onB.GetAsync("/settings/users")).StatusCode);
        using var other = b.Client(TenantSessionWebHost.AlphaHost);
        await other.LoginAsync(owner.Email);

        var token = await browser.AntiforgeryTokenAsync("/settings/users/create");
        var logout = await browser.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        using (var onB = CookieClient(b, copied))
        {
            AssertRedirectsToLogin(await onB.GetAsync("/settings/users"));
            Assert.True(onB.AuthCookie is null, "Instance B must sign the logged-out cookie out.");
        }

        // The session B issued is unaffected, on both instances.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/settings/users")).StatusCode);
        using (var otherOnA = CookieClient(a, other.AuthCookie!))
            Assert.Equal(HttpStatusCode.OK, (await otherOnA.GetAsync("/settings/users")).StatusCode);
        var sessions = b.ReadSessions(storage.AlphaId);
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.RevokedAtUtc is not null);
    }

    [Fact]
    public async Task RecreatedHost_KeepsTheRevocation_AndTheUnaffectedSession()
    {
        using var storage = new TenantSessionStorage();
        string loggedOut;
        string kept;
        IReadOnlyList<(Guid Id, DateTime? RevokedAtUtc)> before;
        Guid ownerId;
        IServiceProvider firstServices;

        await using (var first = await TenantSessionWebHost.StartAsync(storage.Keys, storage: storage))
        {
            firstServices = first.Services;
            ownerId = first.SeedUser(storage.AlphaId, "owner@alpha.test", UserRole.Owner).Id;
            using var a = first.Client(TenantSessionWebHost.AlphaHost);
            await a.LoginAsync("owner@alpha.test");
            using var b = first.Client(TenantSessionWebHost.AlphaHost);
            await b.LoginAsync("owner@alpha.test");
            loggedOut = a.AuthCookie!;
            kept = b.AuthCookie!;
            var token = await a.AntiforgeryTokenAsync("/settings/users/create");
            Assert.Equal(HttpStatusCode.Redirect, (await a.PostFormAsync("/auth/logout", [new("__RequestVerificationToken", token)])).StatusCode);
            before = first.ReadSessions(storage.AlphaId).Select(s => (s.Id, s.RevokedAtUtc)).ToList();
        }

        // The first host and its service provider are gone; a new host is built over the same files and key folder.
        await using var second = await TenantSessionWebHost.StartAsync(storage.Keys, storage: storage);
        Assert.NotSame(firstServices, second.Services);

        using (var replay = CookieClient(second, loggedOut))
        {
            AssertRedirectsToLogin(await replay.GetAsync("/settings/users"));
            Assert.True(replay.AuthCookie is null, "The recreated host must sign the logged-out cookie out.");
        }

        using (var survivor = CookieClient(second, kept))
            Assert.Equal(HttpStatusCode.OK, (await survivor.GetAsync("/settings/users")).StatusCode);

        // Nothing was written after the recreation: the same two sessions, one revoked at the same time.
        Assert.Equal(before, second.ReadSessions(storage.AlphaId).Select(s => (s.Id, s.RevokedAtUtc)).ToList());
        Assert.All(second.ReadSessions(storage.AlphaId), s => Assert.Equal(ownerId, s.UserId));
    }

    private static TenantSessionClient CookieClient(TenantSessionWebHost host, string cookie)
    {
        var client = host.Client(TenantSessionWebHost.AlphaHost);
        client.Cookies[TenantAuthCookieNames.Active] = cookie;
        return client;
    }

    private static void AssertRedirectsToLogin(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        if (location.IsAbsoluteUri)
        {
            Assert.Equal(response.RequestMessage!.Headers.Host, location.Authority);
            location = new Uri(location.PathAndQuery, UriKind.Relative);
        }

        Assert.StartsWith("/auth/login", location.OriginalString, StringComparison.Ordinal);
    }
}
