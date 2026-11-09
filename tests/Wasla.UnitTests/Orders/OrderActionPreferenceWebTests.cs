using System.Net;
using System.Text.Json;
using Wasla.Domain.Enums;
using Wasla.UnitTests.Auth;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The confirmation preference over the real tenant pipeline (WAS-119): who may read and change it, that a change needs
/// antiforgery, that it follows the signed-in employee across requests and sign-ins, and that nothing in the request can
/// point it at another employee or tenant.
/// </summary>
public sealed class OrderActionPreferenceWebTests : IAsyncLifetime
{
    private const string Current = "/order-action-preference/current";
    private const string Update = "/order-action-preference";
    private const string TokenPage = "/orders";

    private TenantSessionWebHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TenantSessionWebHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<TenantSessionClient> SignInAsync(string host, string email)
    {
        var client = _host.Client(host);
        await client.LoginAsync(email);
        return client;
    }

    private static async Task<bool> ReadAsync(TenantSessionClient client)
    {
        var response = await client.GetAsync(Current);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static bool Parse(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("confirmRoutineActions").GetBoolean();

    private static async Task<HttpResponseMessage> SaveAsync(TenantSessionClient client, string value, params KeyValuePair<string, string>[] extra)
    {
        var token = await client.AntiforgeryTokenAsync(TokenPage);
        return await client.PostFormAsync(Update,
            new KeyValuePair<string, string>[] { new("confirmRoutineActions", value), new("__RequestVerificationToken", token) }.Concat(extra));
    }

    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    public async Task EveryRoleThatCanActOnOrders_StartsEnabled_AndCanChangeIt(UserRole role)
    {
        var user = _host.SeedUser(_host.AlphaId, $"{role.ToString().ToLowerInvariant()}@alpha.test", role);
        using var client = await SignInAsync(TenantSessionWebHost.AlphaHost, user.Email);

        Assert.True(await ReadAsync(client));

        var response = await SaveAsync(client, "false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.False(await ReadAsync(client));
    }

    [Fact]
    public async Task Viewer_CannotReadOrChangeIt()
    {
        var viewer = _host.SeedUser(_host.AlphaId, "viewer@alpha.test", UserRole.Viewer);
        using var client = await SignInAsync(TenantSessionWebHost.AlphaHost, viewer.Email);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOrForbiddenAsync(client, Current));
        var token = await client.AntiforgeryTokenAsync(TokenPage);
        var post = await client.PostFormAsync(Update, [new("confirmRoutineActions", "false"), new("__RequestVerificationToken", token)]);
        Assert.Equal(HttpStatusCode.Forbidden, ToForbidden(post));
    }

    [Fact]
    public async Task AnonymousRequests_AreRefused()
    {
        using var client = _host.Client(TenantSessionWebHost.AlphaHost);

        var response = await client.GetAsync(Current);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"An anonymous read answered {response.StatusCode}.");
    }

    [Fact]
    public async Task AChangeWithoutAntiforgery_IsRefused_AndChangesNothing()
    {
        var kitchen = _host.SeedUser(_host.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        using var client = await SignInAsync(TenantSessionWebHost.AlphaHost, kitchen.Email);

        var response = await client.PostFormAsync(Update, [new("confirmRoutineActions", "false")]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await ReadAsync(client));
    }

    [Fact]
    public async Task AChangeWithoutAValue_IsRefused()
    {
        var kitchen = _host.SeedUser(_host.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        using var client = await SignInAsync(TenantSessionWebHost.AlphaHost, kitchen.Email);
        var token = await client.AntiforgeryTokenAsync(TokenPage);

        var response = await client.PostFormAsync(Update, [new("__RequestVerificationToken", token)]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await ReadAsync(client));
    }

    [Fact]
    public async Task TwoEmployeesOfOneTenant_HaveTheirOwnChoice()
    {
        var first = _host.SeedUser(_host.AlphaId, "first@alpha.test", UserRole.Kitchen);
        var second = _host.SeedUser(_host.AlphaId, "second@alpha.test", UserRole.Cashier);
        using var firstClient = await SignInAsync(TenantSessionWebHost.AlphaHost, first.Email);
        using var secondClient = await SignInAsync(TenantSessionWebHost.AlphaHost, second.Email);

        await SaveAsync(firstClient, "false");

        Assert.False(await ReadAsync(firstClient));
        Assert.True(await ReadAsync(secondClient));
    }

    [Fact]
    public async Task SameEmailInTwoTenants_HaveSeparateChoices()
    {
        var alpha = _host.SeedUser(_host.AlphaId, "same@example.test", UserRole.Kitchen);
        var beta = _host.SeedUser(_host.BetaId, "same@example.test", UserRole.Kitchen, id: alpha.Id);
        using var alphaClient = await SignInAsync(TenantSessionWebHost.AlphaHost, alpha.Email);
        using var betaClient = await SignInAsync(TenantSessionWebHost.BetaHost, beta.Email);

        await SaveAsync(alphaClient, "false");

        Assert.False(await ReadAsync(alphaClient));
        Assert.True(await ReadAsync(betaClient));
    }

    [Fact]
    public async Task ForgedUserAndTenantFields_AreIgnored()
    {
        var first = _host.SeedUser(_host.AlphaId, "first@alpha.test", UserRole.Kitchen);
        var second = _host.SeedUser(_host.AlphaId, "second@alpha.test", UserRole.Kitchen);
        using var firstClient = await SignInAsync(TenantSessionWebHost.AlphaHost, first.Email);
        using var secondClient = await SignInAsync(TenantSessionWebHost.AlphaHost, second.Email);

        await SaveAsync(firstClient, "false", new("userId", second.Id.ToString()), new("tenantId", _host.BetaId.ToString()));

        Assert.False(await ReadAsync(firstClient));
        Assert.True(await ReadAsync(secondClient));
    }

    [Fact]
    public async Task TheChoice_SurvivesANewSignIn()
    {
        var kitchen = _host.SeedUser(_host.AlphaId, "kitchen@alpha.test", UserRole.Kitchen);
        using (var client = await SignInAsync(TenantSessionWebHost.AlphaHost, kitchen.Email))
            await SaveAsync(client, "false");

        using var again = await SignInAsync(TenantSessionWebHost.AlphaHost, kitchen.Email);
        Assert.False(await ReadAsync(again));
    }

    // A role without the policy is sent to Access denied, as every tenant page does.
    private static async Task<HttpStatusCode> StatusOrForbiddenAsync(TenantSessionClient client, string path) =>
        ToForbidden(await client.GetAsync(path));

    private static HttpStatusCode ToForbidden(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Redirect
        && (response.Headers.Location?.OriginalString ?? "").Contains("access-denied", StringComparison.OrdinalIgnoreCase)
            ? HttpStatusCode.Forbidden
            : response.StatusCode;
}
