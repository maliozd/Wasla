using System.Globalization;
using System.Net;
using Wasla.UnitTests.Web;

namespace Wasla.UnitTests.Realtime;

/// <summary>
/// WAS-103: the Live Screen page tells its script how often to poll while connected, from Wasla.Web's own internal
/// listener. Without the listener no Wasla.Worker or Wasla.Api change can be pushed, so a connected screen keeps the
/// normal 10 seconds; with it, the lower 30-second safety rate of WAS-24. The page never carries the channel's secret.
/// The script's use of these values is covered by <c>live-screen-invalidation.test.js</c> and
/// <see cref="LiveScreenRealtimeBrowserTests"/>.
/// </summary>
public sealed class LiveScreenPollingRenderingTests
{
    // Obviously fake. Asserted absent from the rendered page.
    private const string FakeSecret = "FAKE-TEST-SECRET-never-render-me-0123456789ab";

    public static TheoryData<string> WithoutListener => new() { "{}", "{\"Disabled\":\"true\"}" };

    [Theory]
    [MemberData(nameof(WithoutListener))]
    public async Task WithoutWebsInternalListener_TheLiveScreenKeepsTheNormalRateWhileConnected(string json)
    {
        var section = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!
            .ToDictionary(v => $"Realtime:Internal:{v.Key}", v => (string?)v.Value);

        var page = await RenderLiveScreenAsync(section);

        Assert.Contains("pollingIntervalMs: 10000,", page, StringComparison.Ordinal);
        Assert.Contains("connectedPollingIntervalMs: 10000,", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithWebsInternalListener_TheLiveScreenPollsLessWhileConnected_AndTheSecretIsNotRendered()
    {
        var page = await RenderLiveScreenAsync(new Dictionary<string, string?>
        {
            ["Realtime:Internal:Port"] = InternalTenantChangeChannelTests.FreeLoopbackPort().ToString(CultureInfo.InvariantCulture),
            ["Realtime:Internal:Secret"] = FakeSecret
        });

        Assert.Contains("pollingIntervalMs: 10000,", page, StringComparison.Ordinal);
        Assert.Contains("connectedPollingIntervalMs: 30000,", page, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecret, page, StringComparison.Ordinal);
    }

    private static async Task<string> RenderLiveScreenAsync(IReadOnlyDictionary<string, string?> configuration)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TenantVisualWebHost.StartAsync(configuration: configuration);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
        {
            BaseAddress = host.BaseAddress
        };
        (await client.GetAsync(TenantVisualWebHost.SignInPath, ct)).EnsureSuccessStatusCode();

        using var response = await client.GetAsync("/orders/live-display", ct);
        var page = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}; {host.ServerErrors}");
        Assert.Null(host.ServerErrors);
        return page;
    }
}
