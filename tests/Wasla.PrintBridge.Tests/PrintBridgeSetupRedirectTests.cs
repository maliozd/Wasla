using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Options;
using Wasla.PrintBridge.Services;

namespace Wasla.PrintBridge.Tests;

/// <summary>
/// WAS-91: setup requests carry the device token, the setup code and the generated credentials, so a redirect is never
/// followed. These go through the real handler of <see cref="PrintBridgeSetupHttpClientFactory"/> over loopback sockets:
/// the server Print Bridge talks to answers with a redirect to a second origin, which must never receive anything.
/// Every value is fake.
/// </summary>
public sealed class PrintBridgeSetupRedirectTests : IDisposable
{
    private const string FakeToken = "redirect-test-token-not-a-real-credential";
    private const string FakeCode = "redirect-test-code-not-a-real-setup-code";

    private readonly LoopbackSetupServer _server = new();
    private readonly LoopbackSetupServer _elsewhere = new();

    public void Dispose()
    {
        _server.Dispose();
        _elsewhere.Dispose();
    }

    public static TheoryData<int> RedirectStatuses => [300, 301, 302, 303, 307, 308];

    [Theory]
    [MemberData(nameof(RedirectStatuses))]
    public async Task EverySetupRequest_ReturnsTheRedirect_AndNothingIsSentToTheOtherOrigin(int status)
    {
        _server.Redirect = new LoopbackRedirect(status, _elsewhere.Url);
        var client = NewClient();
        var installationId = Guid.NewGuid().ToString("D");
        var change = new PrintBridgePendingConnectionChange
        {
            SourceServerUrl = _server.Url,
            SourceToken = FakeToken,
            InstallationId = installationId,
            SetupServerUrl = _elsewhere.Url,
            Code = FakeCode,
            NewToken = PrintBridgePendingConnectionChange.GenerateSecret(),
            CompletionCredential = PrintBridgePendingConnectionChange.GenerateSecret(),
            CreatedAtUtc = DateTime.UtcNow
        };

        var preview = await client.PreviewSetupTransferAsync(
            new WaslaOptions { ServerUrl = _server.Url, AgentToken = FakeToken },
            installationId,
            _elsewhere.Url,
            FakeCode,
            Ct);
        var transfer = await client.TransferSetupAsync(change, Ct);
        var exchange = await client.ExchangeSetupAsync(_server.Url, FakeCode, Ct);
        await client.CompleteSetupAsync(_server.Url, Guid.NewGuid(), change.CompletionCredential, connectionVerified: true, Ct);

        // An unknown answer: the preview fails closed, and the move keeps its pending change for recovery.
        Assert.Equal(WaslaPrintBridgeClient.SetupTransferReplyKind.Unknown, preview.Kind);
        Assert.Equal(WaslaPrintBridgeClient.SetupTransferReplyKind.Unknown, transfer.Kind);
        Assert.Null(exchange);

        // Each request was sent once, to the server it was meant for, and never again: not to the other origin, not
        // with the token, the code, the new token or the completion credential, not even as a GET without a body.
        Assert.Equal(
            ["api/print-bridge/setup/transfer/preview", "api/print-bridge/setup/transfer", "api/print-bridge/setup/exchange", "api/print-bridge/setup/complete"],
            _server.Requests.Select(r => r.Path));
        Assert.Empty(_elsewhere.Requests);
    }

    /// <summary>
    /// The servers above are a real redirect: a handler that follows redirects sends the request, token included, to the
    /// other origin. So the test above fails if the setup handler ever follows one again.
    /// </summary>
    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task TheRedirectAboveIsFollowedByAHandlerThatAllowsRedirects(int status)
    {
        _server.Redirect = new LoopbackRedirect(status, _elsewhere.Url);
        using var following = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_server.Url}/api/print-bridge/setup/transfer");
        request.Headers.TryAddWithoutValidation("X-PrintBridge-Token", FakeToken);
        request.Content = JsonContent.Create(new { code = FakeCode });

        using var response = await following.SendAsync(request, Ct);

        var followed = Assert.Single(_elsewhere.Requests);
        Assert.Equal(FakeToken, followed.Token);
        if (status is 307 or 308)
            Assert.Contains(FakeCode, followed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSetupHandler_NeverFollowsRedirects()
    {
        using var handler = new PrintBridgeSetupHttpClientFactory().CreateHandler(new Uri("https://sushim.app.wasla.tr/"));

        Assert.False(handler.AllowAutoRedirect);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The production setup factory (a real handler); the ordinary API client is never used by these calls.</summary>
    private static WaslaPrintBridgeClient NewClient() =>
        new(
            new HttpClient(new RefuseEverything()),
            new PrintBridgeSettingsHolder(),
            new PrintBridgeSetupHttpClientFactory(),
            NullLogger<WaslaPrintBridgeClient>.Instance,
            "1.0.0.0");

    private sealed class RefuseEverything : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"A setup request used the ordinary API client: {request.RequestUri}");
    }
}
